namespace NightTales.Domain.Rendering;

/// <summary>
/// The dispatch state of a render node (11 README D13, <c>worker_nodes.state</c>): stored as snake_case
/// text with a <c>CHECK</c> listing exactly these members, so a fourth state needs a migration
/// (09 README D8).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Active"/> and <see cref="Down"/> are written by <c>NodeHealthPoller</c> alone (health
/// hysteresis, F12); <see cref="Draining"/> is written by an operator through
/// <c>nts-worker nodes set-state</c> and never by health.
/// </para>
/// <para>
/// The state is not the whole dispatch predicate: a node also needs a fresh, HTTP-200 health answer and
/// a free slot (11.3.3-T2's <c>NodeSelector</c>).
/// </para>
/// </remarks>
public enum WorkerNodeState
{
    /// <summary>The node may receive work once its health is fresh and it has a free slot.</summary>
    Active,

    /// <summary>An operator drained the node (deploy precondition, 11 README D12); health never clears it.</summary>
    Draining,

    /// <summary>No valid health answer for <c>RenderNodes:DownAfterSeconds</c>; only two good pulls revive it.</summary>
    Down,
}
