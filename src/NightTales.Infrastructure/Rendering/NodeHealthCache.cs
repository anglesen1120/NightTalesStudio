using System.Collections.Concurrent;
using NightTales.Application.Abstractions.Rendering;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The in-memory health cache of the Worker (11 README D13): a singleton process-local map from node name
/// to the last valid <c>render-node-health.v1</c> answer, written by <c>NodeHealthPoller</c> and read by
/// 11.3.3-T2's selector and observable gauges.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is persisted: <c>worker_nodes.last_heartbeat_at</c> is the durable half of the same fact,
/// and this map carries what the row does not (reasons, capacity, GPU, disk). A Worker restart therefore
/// starts empty, which is the intended first-30-seconds behaviour of the dispatch rule (D13) — no node is
/// dispatchable until the first pull lands.
/// </para>
/// <para>
/// The map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> because a scrape reads it from an exporter
/// thread while the poller writes it. Values are immutable records, so a reader always sees a consistent
/// snapshot and never a half-updated one.
/// </para>
/// </remarks>
public sealed class NodeHealthCache : INodeHealthCache
{
    private readonly ConcurrentDictionary<string, NodeHealthSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Set(NodeHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.NodeName);

        _snapshots[snapshot.NodeName] = snapshot;
    }

    /// <inheritdoc />
    public bool TryGet(string nodeName, out NodeHealthSnapshot? snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        var found = _snapshots.TryGetValue(nodeName, out var value);
        snapshot = value;

        return found;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<NodeHealthSnapshot> All() => [.. _snapshots.Values];
}
