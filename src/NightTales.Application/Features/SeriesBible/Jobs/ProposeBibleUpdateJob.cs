using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.SeriesBible.Jobs;

/// <summary>
/// The Hangfire job of one bible-proposal run (02.6.3-T2, plan §7.1): <c>default</c> queue, <b>retry 0</b>
/// (D7/F23: transient failures are retried inside the call by the resilience layer, a crash is recovered by
/// <c>StaleGenerationSweeperJob</c>), enqueued by
/// <c>StoryApprovedBibleProposalHandler</c> with ids only (09 README D12, D19).
/// </summary>
/// <remarks>
/// <para>
/// The run id is the only data Hangfire needs to find the work again, but the approved episode travels with it
/// because the run row is series-level: <c>generation_runs.episode_id</c> is <b>NULL</b> for
/// <c>kind = bible_proposal</c> (D2's scope rule, T17), so the episode whose approval bought the call is named
/// by the enqueuing handler rather than read back from the row. The handler asserts it belongs to the run's
/// series before doing anything.
/// </para>
/// <para>
/// The run's owner is not known from the id, so the job resolves it through <see cref="ISystemWorkQuery"/>, then
/// runs the pipeline in a scope pinned to that user with <see cref="ISystemContext.For(Guid)"/>: every query of
/// the run is tenant-filtered like the Api's, with no <c>IgnoreQueryFilters</c>.
/// </para>
/// <para>
/// The class lives in Application, not in the Worker, because the enqueuing handler is Application code and
/// <see cref="IJobScheduler.Enqueue{TJob}"/> names the job class in its expression — the Worker assembly is not
/// visible to it. That is the same placement every other job class of the platform has (09 README D12), and the
/// Worker still owns the composition: it registers the job through
/// <c>BackgroundJobRegistry</c>'s scan and runs it on its Hangfire server.
/// </para>
/// </remarks>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class ProposeBibleUpdateJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<ProposeBibleUpdateJob> logger) : IBackgroundJob
{
    /// <summary>Runs the bible proposal of <paramref name="generationRunId"/>.</summary>
    /// <param name="seriesId">The series the run belongs to; the run row carries it too, and the handler refuses a delivery that disagrees.</param>
    /// <param name="episodeId">The approved episode the proposal was bought for (never the run row's own, which is NULL).</param>
    /// <param name="generationRunId">The run: its owner is what this job resolves before doing anything.</param>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the run reached a status, was held, or was left to another worker.</returns>
    public async Task RunAsync(Guid seriesId, Guid episodeId, Guid generationRunId, CancellationToken cancellationToken)
    {
        Guid? owner = null;

        await foreach (var (_, userId) in systemWork
            .PendingAsync<GenerationRun>(run => run.Id == generationRunId, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            owner = userId;
        }

        if (owner is not { } ownerId)
        {
            LogRunMissing(generationRunId);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        var handler = scope.ServiceProvider.GetRequiredService<ProposeBibleUpdateHandler>();

        await handler.RunAsync(seriesId, episodeId, generationRunId, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6310,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_job_run_missing runId={RunId}")]
    private partial void LogRunMissing(Guid runId);
}
