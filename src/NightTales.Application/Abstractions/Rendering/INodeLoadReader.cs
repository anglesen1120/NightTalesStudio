namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The derived render load per node (11 README D13): how many <c>render_jobs</c> rows are in
/// <c>submitted</c> or <c>running</c> for a node. The poller's drift check compares it with the node's
/// reported <c>capacity.active_tasks</c>, and 11.3.3-T2's free-slot computation reads the same number, so
/// "in flight" is defined once (one implementation, one query shape).
/// </summary>
/// <remarks>
/// Active tasks are deliberately not a column of <c>worker_nodes</c> (00 §6 L311: "active tasks derived
/// from <c>render_jobs</c>"), so this port is the only way to answer the question.
/// </remarks>
public interface INodeLoadReader
{
    /// <summary>
    /// The in-flight render count per node id. A node with no in-flight job is absent from the map (or
    /// present as <c>0</c>); a node id the reader does not know is never invented.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> InFlightByNodeAsync(CancellationToken cancellationToken);
}
