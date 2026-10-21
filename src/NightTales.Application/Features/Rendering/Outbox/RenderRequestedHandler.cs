using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Features.Rendering.Outbox;

/// <summary>
/// Delivers <c>render_requested.v1</c> by enqueueing <see cref="SubmitRenderJob"/> (09 README D11/D12,
/// 09.6.3-T1).
/// </summary>
/// <remarks>
/// <para>
/// It only enqueues: the node call happens in the Worker's job on the <c>render</c> queue, and the outbox row is
/// marked processed as soon as the job is queued, so a slow render node never holds the outbox row's lease. The
/// argument is the <c>render_jobs</c> id alone (D12: job arguments are stored in plain text in the
/// <c>hangfire</c> schema), and the row — not this payload — owns the params, which is what makes a retry days
/// later still correct.
/// </para>
/// <para>
/// Delivery is at least once (D11): a redelivered event enqueues a second job for the same row, and the job's
/// <see cref="RenderJob.AdapterTaskId"/> guard plus the adapter's idempotency key (05 D4) are what keep that from
/// submitting twice.
/// </para>
/// </remarks>
/// <param name="scheduler">The Worker's job port; the Api never resolves this handler.</param>
public sealed class RenderRequestedHandler(IJobScheduler scheduler) : IOutboxHandler<RenderRequested>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(
        RenderRequested domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        scheduler.Enqueue<SubmitRenderJob>((job, ct) => job.RunAsync(domainEvent.RenderJobId, ct));

        return ValueTask.CompletedTask;
    }
}
