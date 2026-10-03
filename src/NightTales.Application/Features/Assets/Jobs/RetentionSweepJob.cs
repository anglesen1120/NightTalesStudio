using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Audit;
using NightTales.Application.Features.Platform.Retention;
using NightTales.Domain.Assets;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Assets.Jobs;

/// <summary>What one sweep run did; the job's summary log line and the tests read it.</summary>
/// <param name="Deleted">Asset objects deleted and rows marked.</param>
/// <param name="Failed">Assets that failed and stay live until tomorrow.</param>
/// <param name="AuditDeleted">Audit rows deleted.</param>
/// <param name="Batches">Batches run across both phases.</param>
/// <param name="Capped">Whether a phase stopped at <c>MaxBatches</c> with backlog left.</param>
/// <param name="Duration">The run's wall time.</param>
public sealed record RetentionSweepSummary(
    int Deleted,
    int Failed,
    int AuditDeleted,
    int Batches,
    bool Capped,
    TimeSpan Duration);

/// <summary>
/// The daily retention sweep (09.7.3-T1): deletes the object behind every live <c>assets</c> row whose
/// <c>retention_until</c> has passed, marks the row <c>deleted_at</c>, and applies the ordered
/// <c>audit_logs</c> rules of <see cref="AuditRetentionRules"/> — in bounded batches, driven by the database
/// only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Progress is guaranteed twice.</b> Each phase stops after <see cref="RetentionSweepOptions.MaxBatches"/>
/// batches (and reports <c>capped</c>), and the asset phase hands every id that failed in this run back to the
/// next query as an exclusion, so a batch in which every row fails cannot be returned forever. A poison object
/// is retried tomorrow.
/// </para>
/// <para>
/// <b>Each row runs in its own DI scope under <see cref="ISystemContext.For"/></b> (09 README D10): the pinned
/// <see cref="ISystemWorkQuery"/> yields only <c>(id, user_id)</c> pairs, so the asset is loaded inside that
/// scope, where the tenant filter resolves to its owner. <c>deleted_at</c> is written after the object delete,
/// so a crash between the two leaves a live row with a missing object (the next run deletes again — the store's
/// delete is idempotent — and marks it), never a row that claims deleted while the file is still served.
/// </para>
/// <para>
/// The job holds a session advisory lock (<see cref="IRetentionSweepLock"/>) so two Workers never sweep at
/// once. Logs carry counts, durations and ids only — never a storage key, a user id or a title (BD16).
/// </para>
/// </remarks>
/// <param name="sweepLock">The single-flight guard.</param>
/// <param name="systemWork">The pinned cross-user read of <c>(id, user_id)</c> pairs.</param>
/// <param name="scopes">Creates the per-row scope.</param>
/// <param name="audit">The audit-log candidate store.</param>
/// <param name="blobs">The object store.</param>
/// <param name="storage">The bucket names (the bound, validated section value).</param>
/// <param name="options">The batch bounds and the enable switch (the bound, validated section value).</param>
/// <param name="clock">The sweep's clock; read once per run.</param>
/// <param name="metrics">The sweep instruments.</param>
/// <param name="logger">The job's log.</param>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class RetentionSweepJob(
    IRetentionSweepLock sweepLock,
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    IAuditRetentionStore audit,
    IBlobStore blobs,
    StorageSettings storage,
    RetentionSweepOptions options,
    IClock clock,
    RetentionSweepMetrics metrics,
    ILogger<RetentionSweepJob> logger) : IBackgroundJob
{
    private const string AssetsPhase = "assets";
    private const string AuditPhase = "audit";

    /// <summary>Runs one sweep. A failed run is retried tomorrow by the schedule, not by Hangfire.</summary>
    public async Task RunAsync(CancellationToken cancellationToken) =>
        _ = await SweepAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Runs one sweep and returns what it did.</summary>
    /// <param name="cancellationToken">Cancels the run between and inside statements.</param>
    public async Task<RetentionSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        var settings = options;
        if (!settings.Enabled)
        {
            LogSkipped("disabled");

            return Empty;
        }

        var started = Stopwatch.GetTimestamp();

        await using var handle = await sweepLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            LogSkipped("locked");

            return Empty;
        }

        var now = clock.UtcNow;
        var counts = new Counts();

        await SweepAssetsAsync(settings, now, counts, cancellationToken).ConfigureAwait(false);

        ExceptionDispatchInfo? auditFailure = null;
        try
        {
            await SweepAuditAsync(settings, now, counts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Asset deletions already committed stay. The failure ends the audit phase, is logged, and is
            // rethrown after the summary so it surfaces in the Hangfire job log (Retries = 0).
            LogAuditPhaseFailed(exception.GetType().Name);
            auditFailure = ExceptionDispatchInfo.Capture(exception);
        }

        var duration = Stopwatch.GetElapsedTime(started);
        metrics.Duration(duration);

        var summary = new RetentionSweepSummary(
            counts.Deleted,
            counts.Failed,
            counts.AuditDeleted,
            counts.Batches,
            counts.Capped,
            duration);
        LogCompleted(summary.Deleted, summary.Failed, summary.AuditDeleted, summary.Batches, summary.Capped, duration.TotalSeconds);

        auditFailure?.Throw();

        return summary;
    }

    private static RetentionSweepSummary Empty { get; } = new(0, 0, 0, 0, false, TimeSpan.Zero);

    private async Task SweepAssetsAsync(
        RetentionSweepOptions settings,
        DateTimeOffset now,
        Counts counts,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<Guid>();
        var iterations = 0;

        while (true)
        {
            iterations++;
            var atCap = iterations > settings.MaxBatches;

            // A snapshot: the exclusion set grows while the batch is processed, the query has to capture one
            // value.
            var excludedIds = excluded.ToList();
            var pairs = new List<(Guid Id, Guid UserId)>();
            await foreach (var pair in systemWork
                .PendingAsync<Asset>(
                    asset => asset.DeletedAt == null
                        && asset.RetentionUntil != null
                        && asset.RetentionUntil < now
                        && !excludedIds.Contains(asset.Id),
                    atCap ? 1 : settings.BatchSize,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                pairs.Add(pair);
            }

            if (atCap)
            {
                // A one-row probe: exactly MaxBatches full batches with nothing left is a finished sweep,
                // not a capped one.
                if (pairs.Count > 0)
                {
                    counts.Capped = true;
                    metrics.Capped(AssetsPhase);
                }

                return;
            }

            counts.Batches++;
            metrics.Batch(AssetsPhase);

            foreach (var (assetId, userId) in pairs)
            {
                switch (await DeleteOneAsync(assetId, userId, now, cancellationToken).ConfigureAwait(false))
                {
                    case AssetOutcome.Deleted:
                        counts.Deleted++;
                        metrics.AssetDeleted();
                        break;
                    case AssetOutcome.Skipped:
                        // Not due any more (or not visible): not a failure, but it must not come back in this run.
                        excluded.Add(assetId);
                        break;
                    default:
                        counts.Failed++;
                        metrics.AssetFailed();
                        excluded.Add(assetId);
                        break;
                }
            }

            if (pairs.Count < settings.BatchSize)
            {
                return;
            }
        }
    }

    /// <summary>Deletes one asset's object and marks its row, inside the owner's scope.</summary>
    /// <remarks>
    /// The due predicate is re-checked on the write context right before the irreversible object delete: the
    /// id query ran on another context, so a row re-stamped (approve re-stamps <c>retention_until</c>) or
    /// soft-deleted in between must not lose its object.
    /// </remarks>
    private async Task<AssetOutcome> DeleteOneAsync(Guid assetId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

            var store = scope.ServiceProvider.GetRequiredService<IAssetRetentionStore>();
            var asset = await store.FindDueAsync(assetId, now, cancellationToken).ConfigureAwait(false);
            if (asset is null)
            {
                LogAssetSkipped(assetId);

                return AssetOutcome.Skipped;
            }

            var deleted = await blobs
                .DeleteAsync(BlobLocator.BucketFor(asset.Kind, storage), asset.StorageKey, cancellationToken)
                .ConfigureAwait(false);
            if (deleted.IsError)
            {
                LogAssetFailed(assetId, deleted.FirstError.Code);

                return AssetOutcome.Failed;
            }

            await store.MarkDeletedAsync(asset, now, cancellationToken).ConfigureAwait(false);

            return AssetOutcome.Deleted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type, never the message: a provider's text can echo the key.
            LogAssetFailed(assetId, exception.GetType().Name);

            return AssetOutcome.Failed;
        }
    }

    private enum AssetOutcome
    {
        Deleted,
        Skipped,
        Failed,
    }

    private async Task SweepAuditAsync(
        RetentionSweepOptions settings,
        DateTimeOffset now,
        Counts counts,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        var iterations = 0;

        while (true)
        {
            iterations++;
            var atCap = iterations > settings.MaxBatches;
            var candidates = await audit
                .LoadAfterAsync(afterCreatedAt, afterId, atCap ? 1 : settings.BatchSize, cancellationToken)
                .ConfigureAwait(false);

            if (atCap)
            {
                // A one-row probe: exactly MaxBatches full batches with no row after the cursor is a finished
                // scan, not a capped one.
                if (candidates.Count > 0)
                {
                    counts.Capped = true;
                    metrics.Capped(AuditPhase);
                }

                return;
            }

            counts.Batches++;
            metrics.Batch(AuditPhase);

            var expired = AuditRetentionPredicate.Expired(candidates, AuditRetentionRules.Ordered, now);
            if (expired.Count > 0)
            {
                var removed = await audit.DeleteAsync(expired, cancellationToken).ConfigureAwait(false);
                counts.AuditDeleted += removed;
                metrics.AuditRowsDeleted(removed);
            }

            if (candidates.Count < settings.BatchSize)
            {
                return;
            }

            // The cursor moves to the last row read on every iteration, so no row is read twice and a
            // batch that kept its rows (retained by their rule) is simply skipped by it.
            var last = candidates[^1];
            afterCreatedAt = last.CreatedAt;
            afterId = last.Id;
        }
    }

    [LoggerMessage(
        EventId = 2410,
        Level = LogLevel.Information,
        Message = "retention_sweep_skipped reason={Reason}")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(
        EventId = 2411,
        Level = LogLevel.Warning,
        Message = "retention_sweep_asset_failed assetId={AssetId} code={Code}")]
    private partial void LogAssetFailed(Guid assetId, string code);

    [LoggerMessage(
        EventId = 2414,
        Level = LogLevel.Information,
        Message = "retention_sweep_asset_skipped assetId={AssetId} reason=not_due_or_not_visible")]
    private partial void LogAssetSkipped(Guid assetId);

    [LoggerMessage(
        EventId = 2412,
        Level = LogLevel.Error,
        Message = "retention_sweep_audit_phase_failed exceptionType={ExceptionType}")]
    private partial void LogAuditPhaseFailed(string exceptionType);

    [LoggerMessage(
        EventId = 2413,
        Level = LogLevel.Information,
        Message = "retention_sweep_completed deleted={Deleted} failed={Failed} auditDeleted={AuditDeleted} batches={Batches} capped={Capped} seconds={Seconds:F3}")]
    private partial void LogCompleted(int deleted, int failed, int auditDeleted, int batches, bool capped, double seconds);

    private sealed class Counts
    {
        public int Deleted { get; set; }

        public int Failed { get; set; }

        public int AuditDeleted { get; set; }

        public int Batches { get; set; }

        public bool Capped { get; set; }
    }
}
