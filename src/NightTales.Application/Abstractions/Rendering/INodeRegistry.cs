using NightTales.Domain.Rendering;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The read seam over the <c>worker_nodes</c> registry (11 README D13): the seeder checks which rows
/// exist, the poller enumerates them each tick, and the <c>nts-worker nodes list</c> verb prints them. It
/// reads through 09 README D8's no-tracking <c>IReadDb</c>, so a caller can never mutate a row by accident;
/// writes go through <c>IAppDbContext</c> and the domain methods.
/// </summary>
public interface INodeRegistry
{
    /// <summary>
    /// Every registered node, ordered by <see cref="WorkerNode.Name"/> (ordinal), so the CLI and the poller
    /// walk a deterministic list.
    /// </summary>
    Task<IReadOnlyList<WorkerNode>> AllAsync(CancellationToken cancellationToken);
}
