using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Rendering.Jobs;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The seam that hands an <c>ingesting</c> render job to the output-ingest step (03.4.2-T1/WF14): the poller
/// calls it once the job's transition to <c>ingesting</c> is committed, never before.
/// </summary>
/// <remarks>
/// <para>
/// 09.6.3-T2 registers <see cref="SchedulerRenderIngestEnqueuer"/>, which enqueues
/// <see cref="IngestRenderOutputJob"/> on the <c>render</c> queue (D12). The poller therefore depends on this
/// seam instead of on the job type, so the split of submit+poll from ingest (WF14) stays a registration
/// change.
/// </para>
/// <para>
/// The id is the only argument (D12, ids-only job arguments): the enqueuer must not carry the job's params,
/// the node or anything a log could echo.
/// </para>
/// </remarks>
public interface IRenderIngestEnqueuer
{
    /// <summary>Queues the output ingest of <paramref name="renderJobId"/>.</summary>
    /// <param name="renderJobId">The committed <c>ingesting</c> render job.</param>
    void Enqueue(Guid renderJobId);
}

/// <summary>
/// The Worker's <see cref="IRenderIngestEnqueuer"/> (09 README D12, WF14): it queues
/// <see cref="IngestRenderOutputJob"/> on the <c>render</c> queue the moment the poller has committed the
/// row's transition to <c>ingesting</c>.
/// </summary>
/// <remarks>
/// <para>
/// The only argument is the <c>render_jobs</c> id (D12: job arguments are stored in plain text in the
/// <c>hangfire</c> schema); the row — not the payload — owns the params, the node and the adapter task, which
/// is what makes a retry that runs the job again still correct. Delivery is at least once, and the job's own
/// idempotency is what keeps a redelivery from ingesting twice.
/// </para>
/// <para>
/// It is registered beside <see cref="IJobScheduler"/> and resolved from a scope the way a job is, so the
/// poller never depends on the Hangfire client directly (the Api's port throws by design and never composes
/// this graph).
/// </para>
/// </remarks>
/// <param name="scheduler">The Worker's job port; the job expression is built here, never in the poller.</param>
public sealed class SchedulerRenderIngestEnqueuer(IJobScheduler scheduler) : IRenderIngestEnqueuer
{
    /// <inheritdoc />
    public void Enqueue(Guid renderJobId) =>
        scheduler.Enqueue<IngestRenderOutputJob>((job, ct) => job.RunAsync(renderJobId, ct));
}
