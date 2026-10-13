using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Domain.Research.Events;

namespace NightTales.Application.Features.Research.Jobs;

/// <summary>
/// Delivers <c>fact_sheet_generation_requested.v1</c> by enqueueing <see cref="GenerateFactSheetJob"/>
/// (02.5.3 plan §7, D21): it only enqueues, with the run id as the single data argument, so the outbox row is marked
/// processed as soon as the job is queued.
/// </summary>
/// <remarks>
/// Delivery is at least once, so the job can start twice for one run. The job is idempotent on the run: a terminal
/// run is logged and skipped, and a fresh running run belongs to the first worker.
/// </remarks>
/// <param name="scheduler">The job scheduler.</param>
public sealed class GenerateFactSheetJobHandler(IJobScheduler scheduler) : IOutboxHandler<FactSheetGenerationRequested>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(
        FactSheetGenerationRequested domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        scheduler.Enqueue<GenerateFactSheetJob>((job, jobToken) => job.RunAsync(domainEvent.GenerationRunId, jobToken));

        return ValueTask.CompletedTask;
    }
}
