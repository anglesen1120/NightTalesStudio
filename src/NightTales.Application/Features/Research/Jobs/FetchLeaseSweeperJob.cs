using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Application.Telemetry;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Jobs;

/// <summary>What one sweep pass did.</summary>
/// <param name="Requeued">Expired leases that went back to <c>queued</c> with a backoff.</param>
/// <param name="Failed">Expired leases that reached the attempt cap and became <c>failed</c> (<c>source_unreachable</c>).</param>
/// <param name="SourcesFailed">Sources moved to <c>failed</c> because their task failed.</param>
public sealed record FetchLeaseSweepSummary(int Requeued, int Failed, int SourcesFailed);

/// <summary>
/// Requeues the leases the fetcher abandoned (02.5.2-T2, 02 D10; recurring every minute, id <c>fetch-lease-sweeper</c>).
/// One statement moves every <c>leased</c> task past <c>lease_until</c> back to <c>queued</c> with a backoff, or to
/// <c>failed</c> / <c>source_unreachable</c> once <c>attempts</c> reached <c>Fetcher:MaxAttempts</c>; then every task that
/// failed gets its source moved to <c>failed</c> with the same code, so the UI shows a failed source and not a task that
/// silently vanished.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-instance safety without a lock.</b> Hangfire recurring jobs are per server, so two Workers may run this job at
/// once. That is safe: the statement's guard <c>WHERE status = 'leased' AND lease_until &lt; now()</c> flips each row's
/// status, so a row is updated by exactly one run and the second run finds nothing; no distributed lock and no
/// <c>DisableConcurrentExecution</c> is needed. Re-running is idempotent for the same reason.
/// </para>
/// <para>
/// Each failed task's source is handled in its own scope pinned to the task's owner with <see cref="ISystemContext.For(Guid)"/>
/// and its own transaction; a source that is already terminal (or was deleted, which cascades its task away) is skipped. A
/// source that cannot be saved is logged by type and left; one bad row never ends the pass. Logs and metrics carry counts and
/// ids only (D19): never a URL, a title or a text.
/// </para>
/// </remarks>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class FetchLeaseSweeperJob(
    IFetchLeaseSweepStore store,
    IServiceScopeFactory scopes,
    FetchLeaseSweeperOptions options,
    IClock clock,
    NightTalesMetrics metrics,
    FetchLeaseGauge gauge,
    ILogger<FetchLeaseSweeperJob> logger) : IBackgroundJob
{
    /// <summary>The metric status of a task that went back to the queue.</summary>
    public const string RequeuedStatus = "requeued";

    /// <summary>The metric status of a task that reached its attempt cap.</summary>
    public const string FailedStatus = "failed";

    /// <summary>How far back (hours) the reconcile step looks for a failed task whose source is still pending.</summary>
    public const int ReconcileLookbackHours = 24;

    /// <summary>The most tasks one reconcile step repairs.</summary>
    public const int ReconcileLimit = 50;

    /// <summary>Runs one sweep pass (the recurring job's entry).</summary>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task RunAsync(CancellationToken cancellationToken) =>
        _ = await SweepAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Runs one sweep pass.</summary>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>What the pass did.</returns>
    public async Task<FetchLeaseSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        var swept = await store
            .RequeueExpiredLeasesAsync(options.MaxAttempts, options.RequeueBackoffSeconds, cancellationToken)
            .ConfigureAwait(false);

        if (swept.IsError)
        {
            // A transient database failure or a statement deadline: nothing was changed, the next tick retries.
            LogUnavailable(swept.FirstError.Code);

            return new FetchLeaseSweepSummary(0, 0, 0);
        }

        var requeued = 0;
        var failed = 0;
        var sourcesFailed = 0;
        var now = clock.UtcNow;

        foreach (var row in swept.Value)
        {
            if (!row.Failed)
            {
                requeued++;
                metrics.RecordFetchTask(RequeuedStatus, null);

                continue;
            }

            failed++;
            metrics.RecordFetchTask(FailedStatus, ErrorCodes.SourceUnreachable);

            // The task's source is failed by the reconcile step below, which also repairs a crash between the two writes.
        }

        sourcesFailed += await ReconcileAsync(now, cancellationToken).ConfigureAwait(false);
        await RefreshGaugeAsync(cancellationToken).ConfigureAwait(false);

        LogCompleted(requeued, failed, sourcesFailed);

        return new FetchLeaseSweepSummary(requeued, failed, sourcesFailed);
    }

    /// <summary>
    /// Heals a crash between the task flip and the source update: a task that is <c>failed</c> while its source is still
    /// <c>pending</c> gets the source failed with the task's own code, in the same bounded pass, so the next tick repairs it.
    /// </summary>
    private async Task<int> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stuck = await store
            .ListFailedTasksWithPendingSourceAsync(ReconcileLookbackHours, ReconcileLimit, cancellationToken)
            .ConfigureAwait(false);
        if (stuck.IsError)
        {
            LogUnavailable(stuck.FirstError.Code);

            return 0;
        }

        var healed = 0;
        foreach (var row in stuck.Value)
        {
            var code = string.IsNullOrWhiteSpace(row.ErrorCode) ? ErrorCodes.SourceUnreachable : row.ErrorCode;
            if (await FailSourceAsync(row.Id, row.UserId, row.EpisodeId, row.SourceId, code, now, cancellationToken)
                .ConfigureAwait(false))
            {
                healed++;
            }
        }

        return healed;
    }

    /// <summary>Sets <c>fetch_leases_active</c> from the database; this process is the one that changes leases in bulk.</summary>
    private async Task RefreshGaugeAsync(CancellationToken cancellationToken)
    {
        var leased = await store.CountLeasedAsync(cancellationToken).ConfigureAwait(false);
        if (leased.IsError)
        {
            LogUnavailable(leased.FirstError.Code);

            return;
        }

        gauge.Set(leased.Value);
    }

    private async Task<bool> FailSourceAsync(
        Guid taskId,
        Guid userId,
        Guid episodeId,
        Guid sourceId,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

            // Two Workers may reconcile the same row: this no-op UPDATE takes the row lock, so the second one waits for
            // the first commit, re-evaluates "still pending" and skips - the source is failed (and the event raised) once.
            var locked = await db.StorySources
                .Where(candidate => candidate.Id == sourceId && candidate.Status == StorySourceStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.Status, candidate => candidate.Status),
                    cancellationToken)
                .ConfigureAwait(false);
            if (locked == 0)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return false;
            }

            var source = await db.StorySources
                .SingleOrDefaultAsync(candidate => candidate.Id == sourceId, cancellationToken)
                .ConfigureAwait(false);
            if (source is not { Status: StorySourceStatus.Pending })
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return false;
            }

            source.MarkFailed(errorCode, null, now);

            // The same transaction raises the event, so the outbox row commits with the flip and the UI refetches the list.
            var episode = await db.Episodes
                .SingleOrDefaultAsync(candidate => candidate.Id == episodeId, cancellationToken)
                .ConfigureAwait(false);
            episode?.RaiseSourceFetched(sourceId, now);

            var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (committed.IsError)
            {
                LogSourceFailed(taskId, committed.FirstError.Code);

                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSourceFailed(taskId, exception.GetType().Name);

            return false;
        }
    }

    [LoggerMessage(
        EventId = 6150,
        Level = LogLevel.Warning,
        Message = "fetch_lease_sweep_unavailable code={Code}")]
    private partial void LogUnavailable(string code);

    [LoggerMessage(
        EventId = 6151,
        Level = LogLevel.Warning,
        Message = "fetch_lease_sweep_source_failed taskId={TaskId} reason={Reason}")]
    private partial void LogSourceFailed(Guid taskId, string reason);

    [LoggerMessage(
        EventId = 6152,
        Level = LogLevel.Information,
        Message = "fetch_lease_sweep_completed requeued={Requeued} failed={Failed} sourcesFailed={SourcesFailed}")]
    private partial void LogCompleted(int requeued, int failed, int sourcesFailed);
}
