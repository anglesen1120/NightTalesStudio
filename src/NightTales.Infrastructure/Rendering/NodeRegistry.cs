using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The <c>worker_nodes</c> read seam of 11 README D13, over 09 README D8's no-tracking <c>IReadDb</c>.
/// </summary>
/// <remarks>
/// The ordering is ordinal by name so the CLI output and the poller's log lines are stable across runs.
/// The query carries no tenant predicate by design: <see cref="WorkerNode"/> is not <c>IUserOwned</c>, so
/// 09 README D10's filter leaves it alone and no <c>IgnoreQueryFilters</c> bypass is involved.
/// </remarks>
/// <param name="readDb">The scope's read model.</param>
public sealed class NodeRegistry(IReadDb readDb) : INodeRegistry
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkerNode>> AllAsync(CancellationToken cancellationToken) =>
        await readDb.Query<WorkerNode>()
            .OrderBy(node => node.Name)
            .ToListAsync(cancellationToken);
}
