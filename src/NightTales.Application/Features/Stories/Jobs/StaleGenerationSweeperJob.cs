using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Stories.Jobs;

/// <summary>What one sweep pass did.</summary>
/// <param name="Failed">Running runs past their deadline that were failed (branch a).</param>
/// <param name="Cancelled">Queued runs older than 12 hours that were cancelled (branch b).</param>
/// <param name="Skipped">Runs that were no longer stale or no longer open when the sweep reached them.</param>
public sealed record StaleSweepSummary(int Failed, int Cancelled, int Skipped);

/// <summary>
/// Recovers every kind of crashed or stuck generation run (02.2.2-T3, plan §7.3; recurring every 5 minutes, id
/// <c>stale-generation-sweeper</c>). Branch (a) fails a <c>running</c> run past <c>started_at + timeout + 60 s</c> with the
/// kind's generic code; branch (b) cancels a <c>queued</c> run that never started within 12 hours with
/// <c>story_generation_cancelled</c> (not a user cancel). Both set <c>counts_toward_limit = false</c>,
/// <c>error_detail = 'stale_run_sweep'</c>, refund the reservation with reason <c>stale_run</c>, and move the episode back to
/// its previous status only for a <c>story</c> run with an episode; every other kind never touches an episode.
/// </summary>
/// <remarks>
/// <para>
/// The ids and owners come from <see cref="ISystemWorkQuery"/>; each run is then handled in its own scope pinned to the
/// owner with <see cref="ISystemContext.For(Guid)"/>, like the retention sweep. A row that cannot be handled is logged
/// by type and left for the next pass; one bad row never ends the pass.
/// </para>
/// <para>
/// <b>Hand-over (12.8.3):</b> the stuck-queued cancel e-mail has no sender in <c>src</c> (no template and no request path
/// for it), so none is sent here; 12.8.3 adds it at <c>CancelQueuedAsync</c> after the cancel commits.
/// </para>
/// </remarks>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class StaleGenerationSweeperJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    StoryJobOptions jobOptions,
    StorySweeperOptions options,
    IClock clock,
    ILogger<StaleGenerationSweeperJob> logger) : IBackgroundJob
{
    /// <summary>A queued run that never started within this time is cancelled (12.8.3, cancel-after-12-h).</summary>
    public static readonly TimeSpan QueuedLimit = TimeSpan.FromHours(12);

    /// <summary>The detail written on every swept run.</summary>
    public const string StaleDetail = "stale_run_sweep";

    /// <summary>The refund reason of a swept run.</summary>
    public const string StaleRefundReason = "stale_run";

    /// <summary>Runs one sweep pass (the recurring job's entry).</summary>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    public async Task RunAsync(CancellationToken cancellationToken) =>
        _ = await SweepAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Runs one sweep pass.</summary>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>What the pass did.</returns>
    public async Task<StaleSweepSummary> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var batch = options.BatchSize;
        var failed = 0;
        var cancelled = 0;
        var skipped = 0;

        // The smallest deadline of any tier; the exact one (Long is longer) is checked per row under its own scope.
        var runningCutoff = now - (TimeSpan.FromSeconds(jobOptions.TimeoutSeconds) + StoryGenerationPipeline.SweepGrace);
        var queuedCutoff = now - QueuedLimit;

        var excluded = new List<Guid>();

        // Branch (a): running past the deadline.
        while (true)
        {
            var snapshot = excluded.ToList();
            var rows = await ReadAsync(
                RunningPastDeadline(runningCutoff, snapshot),
                batch,
                cancellationToken).ConfigureAwait(false);

            foreach (var (runId, userId) in rows)
            {
                if (await FailRunningAsync(runId, userId, now, cancellationToken).ConfigureAwait(false))
                {
                    failed++;
                }
                else
                {
                    skipped++;
                    excluded.Add(runId);
                }
            }

            if (rows.Count < batch)
            {
                break;
            }
        }

        // Branch (b): queued and never started for 12 hours.
        while (true)
        {
            var snapshot = excluded.ToList();
            var rows = await ReadAsync(
                QueuedPastLimit(queuedCutoff, snapshot),
                batch,
                cancellationToken).ConfigureAwait(false);

            foreach (var (runId, userId) in rows)
            {
                if (await CancelQueuedAsync(runId, userId, now, cancellationToken).ConfigureAwait(false))
                {
                    cancelled++;
                }
                else
                {
                    skipped++;
                    excluded.Add(runId);
                }
            }

            if (rows.Count < batch)
            {
                break;
            }
        }

        LogCompleted(failed, cancelled, skipped);

        return new StaleSweepSummary(failed, cancelled, skipped);
    }

    /// <summary>The generic <c>*_generation_failed</c> code of a run kind (D8).</summary>
    /// <param name="kind">The run kind.</param>
    /// <returns>The code the sweeper writes for a run of <paramref name="kind"/>.</returns>
    public static string FailureCodeFor(GenerationKind kind) => kind switch
    {
        GenerationKind.Story => ErrorCodes.StoryGenerationFailed,
        GenerationKind.FactSheet => ErrorCodes.FactSheetGenerationFailed,
        GenerationKind.BibleProposal => ErrorCodes.BibleProposalGenerationFailed,
        GenerationKind.Ideas => ErrorCodes.IdeasGenerationFailed,
        GenerationKind.FindSources => ErrorCodes.FindSourcesGenerationFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown generation kind."),
    };

    /// <summary>
    /// The branch (a) predicate. The time part is <c>(started_at ?? queued_at)</c>, which EF translates to
    /// <c>COALESCE(started_at, queued_at)</c>: the expression of the sweeper index <c>(status, COALESCE(started_at, queued_at))</c>
    /// (migration <c>02_GenerationRuns</c>), so the scan is bounded by the cutoff instead of filtering every running run.
    /// </summary>
    /// <param name="cutoff">Runs that started before this are past the smallest deadline.</param>
    /// <param name="excluded">Runs this pass already skipped.</param>
    /// <returns>The filter.</returns>
    public static System.Linq.Expressions.Expression<Func<GenerationRun, bool>> RunningPastDeadline(
        DateTimeOffset cutoff,
        IReadOnlyCollection<Guid> excluded) =>
        // No `StartedAt != null` here: EF would fold it into the comparison and drop the COALESCE the index is built on.
        // A running row always has started_at; FailRunningAsync re-checks it per row.
        run => run.Status == GenerationRunStatus.Running
            && (run.StartedAt ?? run.QueuedAt) < cutoff
            && !excluded.Contains(run.Id);

    /// <summary>The branch (b) predicate, on the same indexed <c>COALESCE(started_at, queued_at)</c> expression as <see cref="RunningPastDeadline"/>.</summary>
    /// <param name="cutoff">Queued runs older than this are stuck.</param>
    /// <param name="excluded">Runs this pass already skipped.</param>
    /// <returns>The filter.</returns>
    public static System.Linq.Expressions.Expression<Func<GenerationRun, bool>> QueuedPastLimit(
        DateTimeOffset cutoff,
        IReadOnlyCollection<Guid> excluded) =>
        run => run.Status == GenerationRunStatus.Queued
            && (run.StartedAt ?? run.QueuedAt) < cutoff
            && !excluded.Contains(run.Id);

    private async Task<List<(Guid Id, Guid UserId)>> ReadAsync(
        System.Linq.Expressions.Expression<Func<GenerationRun, bool>> where,
        int batch,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, Guid UserId)>();

        await foreach (var row in systemWork.PendingAsync(where, batch, cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    private async Task<bool> FailRunningAsync(Guid runId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var progress = scope.ServiceProvider.GetRequiredService<StoryGenerationProgress>();

            var run = await db.GenerationRuns.SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is not { Status: GenerationRunStatus.Running, StartedAt: { } started })
            {
                return false;
            }

            // The exact deadline: the Long tier runs longer than the cutoff the query used.
            var tier = run.EpisodeId is { } episodeId
                ? await db.Episodes
                    .Where(episode => episode.Id == episodeId)
                    .Select(episode => (LengthTier?)episode.LengthTier)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var window = jobOptions.TimeoutFor(tier ?? LengthTier.Short) + StoryGenerationPipeline.SweepGrace;
            var deadline = started + window;
            if (deadline >= now)
            {
                return false;
            }

            var failure = new StoryFailure(FailureCodeFor(run.Kind), StaleDetail, false, StaleRefundReason);
            // The claim repeats "running, and started before now - window" inside the atomic update: a run a worker restarted
            // since the read above is skipped, not failed.
            var ended = await progress
                .FailAsync(runId, failure, cancellationToken, recovery: true, stale: new StaleClaim(GenerationRunStatus.Running, now - window))
                .ConfigureAwait(false);

            return !ended.IsError;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRowFailed(runId, "running", exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> CancelQueuedAsync(Guid runId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var progress = scope.ServiceProvider.GetRequiredService<StoryGenerationProgress>();

            var run = await db.GenerationRuns.SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is not { Status: GenerationRunStatus.Queued } || run.QueuedAt >= now - QueuedLimit)
            {
                return false;
            }

            // 12.8.3 hand-over: the cancel e-mail is sent here once that task adds a sender.
            var ended = await progress
                .CancelAsync(
                    runId,
                    StaleRefundReason,
                    ErrorCodes.StoryGenerationCancelled,
                    StaleDetail,
                    cancellationToken,
                    recovery: true,
                    stale: new StaleClaim(GenerationRunStatus.Queued, now - QueuedLimit))
                .ConfigureAwait(false);

            return !ended.IsError;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRowFailed(runId, "queued", exception.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(
        EventId = 6140,
        Level = LogLevel.Warning,
        Message = "stale_generation_sweep_row_failed runId={RunId} branch={Branch} exceptionType={ExceptionType}")]
    private partial void LogRowFailed(Guid runId, string branch, string exceptionType);

    [LoggerMessage(
        EventId = 6141,
        Level = LogLevel.Information,
        Message = "stale_generation_sweep_completed failed={Failed} cancelled={Cancelled} skipped={Skipped}")]
    private partial void LogCompleted(int failed, int cancelled, int skipped);
}
