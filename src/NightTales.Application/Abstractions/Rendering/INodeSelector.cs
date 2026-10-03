using ErrorOr;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The single decision point of render dispatch (11 README D13, 11.3.3-T2): which node takes the next render.
/// </summary>
/// <remarks>
/// <para>
/// A node is dispatchable only when it is <c>active</c>, its last health answer is fresh and HTTP 200, it
/// reports <c>capacity.accepting</c>, and it has more free slots than it is running. The highest free-slot
/// count wins and ties go to the lowest <c>name</c> (ordinal), so two dispatchers agree without coordination.
/// </para>
/// <para>
/// The selector is read-only and reserves nothing: the <c>submitted</c> row 09.6.3-T1 inserts is what consumes
/// the slot, and a double pick on the last slot resolves as the adapter's 429 <c>adapter_capacity_full</c>.
/// </para>
/// </remarks>
public interface INodeSelector
{
    /// <summary>
    /// Picks the node with the most free slots. No candidate is not an exception: it is the
    /// <c>render_worker_unavailable</c> error, a retry signal — the job stays <c>queued</c> and 09.6.3
    /// reschedules <c>SubmitRenderJob</c> after <c>RenderNodes:HealthPollSeconds</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The chosen node (its base URL and pin feed the adapter client), or <c>render_worker_unavailable</c>.</returns>
    Task<ErrorOr<WorkerNode>> PickAsync(CancellationToken cancellationToken);
}
