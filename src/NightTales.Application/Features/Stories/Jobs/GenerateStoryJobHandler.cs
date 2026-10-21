using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Domain.Stories.Generation.Events;

namespace NightTales.Application.Features.Stories.Jobs;

/// <summary>
/// Delivers <c>story_generation_requested.v1</c> by enqueueing <see cref="GenerateStoryJob"/> (02.2.2-T3, D21): it only
/// enqueues, with the run id as the single data argument, so the outbox row is marked processed as soon as the job is queued.
/// </summary>
/// <remarks>
/// Delivery is at least once, so the job can start twice for one run. The job is idempotent on the run: a terminal run is
/// logged and skipped, and a fresh running run belongs to the first worker.
/// </remarks>
/// <param name="scheduler">The job scheduler.</param>
public sealed class GenerateStoryJobHandler(IJobScheduler scheduler) : IOutboxHandler<StoryGenerationRequested>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(
        StoryGenerationRequested domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        scheduler.Enqueue<GenerateStoryJob>((job, jobToken) => job.RunAsync(domainEvent.GenerationRunId, jobToken));

        return ValueTask.CompletedTask;
    }
}
