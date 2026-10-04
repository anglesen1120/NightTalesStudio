using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Stories.Jobs;

/// <summary>
/// The Hangfire job of one story run (02.2.2-T3, plan §7.1): <c>default</c> queue, <b>retry 0</b> (F23/D7: transient
/// failures are retried inside the call by the resilience layer, a crash is recovered by
/// <see cref="StaleGenerationSweeperJob"/>), enqueued by <see cref="GenerateStoryJobHandler"/>. The run id is the only data
/// argument (ids-only architecture rule, D19).
/// </summary>
/// <remarks>
/// The run's owner is not known from the id, so the job resolves it through <see cref="ISystemWorkQuery"/>, then runs the
/// pipeline in a scope pinned to that user with <see cref="ISystemContext.For(Guid)"/>: every query of the run is
/// tenant-filtered like the Api's, with no <c>IgnoreQueryFilters</c>.
/// </remarks>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class GenerateStoryJob(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ILogger<GenerateStoryJob> logger) : IBackgroundJob
{
    /// <summary>Runs the story generation of <paramref name="generationRunId"/>.</summary>
    /// <param name="generationRunId">The run (the only parameter besides the token).</param>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the run reached a status, was held, or was left to another worker.</returns>
    public async Task RunAsync(Guid generationRunId, CancellationToken cancellationToken)
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

        var pipeline = scope.ServiceProvider.GetRequiredService<StoryGenerationPipeline>();

        await pipeline.RunAsync(generationRunId, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 6160,
        Level = LogLevel.Warning,
        Message = "generate_story_job_run_missing runId={RunId}")]
    private partial void LogRunMissing(Guid runId);
}
