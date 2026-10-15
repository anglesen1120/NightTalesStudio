using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.SeriesBible.Events;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;

namespace NightTales.Application.Features.SeriesBible.Jobs;

/// <summary>
/// The Hangfire job of one deferred bible-proposal delivery (02.6.3-T2, plan §7.1 step 7): <c>default</c> queue,
/// <b>retry 0</b> — the delay is the retry, and the handler re-schedules at most ten times before it drops the
/// proposal. It is scheduled by <c>StoryApprovedBibleProposalHandler</c> when the series-level active-run index
/// holds another proposal of the same series, and re-enters that handler with ids only (09 README D12, D19).
/// </summary>
/// <remarks>
/// The handoff carries no user id, so the job resolves the owner of the approved episode through
/// <see cref="ISystemWorkQuery"/> and pins <see cref="ISystemContext.For(Guid)"/> before the handler runs: the
/// deferred delivery then reads and writes the owner's rows only, under the same tenant filters as the original
/// outbox delivery (D10). The episode is the row whose approval bought the work, and its owner is the series'
/// owner — an episode that is gone makes the deferred work moot, so the job logs and stops.
/// </remarks>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class BibleProposalHandoffJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<BibleProposalHandoffJob> logger) : IBackgroundJob
{
    /// <summary>Re-runs the queueing work of one deferred approval.</summary>
    /// <param name="seriesId">The series the proposal belongs to.</param>
    /// <param name="episodeId">The approved episode the proposal is built from.</param>
    /// <param name="storyVersionId">The approved story version, the other half of the idempotency key.</param>
    /// <param name="approvalSource">Who approved the story: the Domain enum (an ids-only job parameter).</param>
    /// <param name="tries">How many deliveries this work already spent, counting the outbox delivery as the first.</param>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the work was queued, skipped, deferred again or dropped.</returns>
    public async Task RunAsync(
        Guid seriesId,
        Guid episodeId,
        Guid storyVersionId,
        ApprovalSource approvalSource,
        int tries,
        CancellationToken cancellationToken)
    {
        Guid? owner = null;

        await foreach (var (_, userId) in systemWork
            .PendingAsync<Episode>(episode => episode.Id == episodeId, 1, cancellationToken)
            .ConfigureAwait(false))
        {
            owner = userId;
        }

        if (owner is not { } ownerId)
        {
            LogEpisodeMissing(episodeId);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(ownerId);

        var handler = scope.ServiceProvider.GetRequiredService<StoryApprovedBibleProposalHandler>();

        await handler
            .HandleHandoffAsync(
                new BibleProposalHandoff(seriesId, episodeId, storyVersionId, approvalSource, tries),
                cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6311,
        Level = LogLevel.Warning,
        Message = "bible_proposal_handoff_job_episode_missing episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid episodeId);
}
