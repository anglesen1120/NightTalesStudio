using NightTales.RenderNode.Contract;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// One node's last valid health answer (11 README D13), as the poller cached it: the dispatch predicate
/// (11.3.3-T2's <c>NodeSelector</c>) and the observable node gauges read this and never call a node
/// themselves (Q1).
/// </summary>
/// <param name="NodeName">The node's name — the metric tag, never a URL or a key.</param>
/// <param name="PulledAt">When the Worker received the answer, from the Worker's clock (09 README D3).</param>
/// <param name="HttpOk">Whether the answer was HTTP 200 (a 503 body is a valid answer but not dispatchable).</param>
/// <param name="Status">The body's <c>status</c> literal (<c>ok</c>/<c>error</c>).</param>
/// <param name="Reasons">The body's <c>reasons</c> — empty means <c>status = "ok"</c> (05 README D4).</param>
/// <param name="MaxConcurrentTasks">The body's <c>capacity.max_concurrent_tasks</c>.</param>
/// <param name="ActiveTasks">The body's <c>capacity.active_tasks</c>, compared with the derived count for the drift alert.</param>
/// <param name="Accepting">The body's <c>capacity.accepting</c>; false whenever the status is not <c>ok</c>.</param>
/// <param name="GpuUtilPct">The cached GPU utilisation, or <c>null</c> when the node could not sample it.</param>
/// <param name="GpuMemUsedMb">The cached GPU memory in use, or <c>null</c>.</param>
/// <param name="DiskUsedPct">The cached task-dir disk use, or <c>null</c>.</param>
/// <param name="GpuTempC">The cached GPU temperature in degrees Celsius, or <c>null</c> when the node could not sample it (T28).</param>
public sealed record NodeHealthSnapshot(
    string NodeName,
    DateTimeOffset PulledAt,
    bool HttpOk,
    string Status,
    IReadOnlyList<string> Reasons,
    int MaxConcurrentTasks,
    int ActiveTasks,
    bool Accepting,
    double? GpuUtilPct,
    long? GpuMemUsedMb,
    double? DiskUsedPct,
    int? GpuTempC = null)
{
    /// <summary>
    /// Projects the parsed <c>render-node-health.v1</c> body into the cache shape (D13, D18): the wire
    /// reader is <c>RenderNodeJson.Tolerant</c>, so an additive v1 member never fails a poll.
    /// </summary>
    /// <param name="nodeName">The node that answered.</param>
    /// <param name="health">The parsed body.</param>
    /// <param name="pulledAt">The instant the Worker received it, from the Worker's clock.</param>
    /// <param name="httpOk">Whether the response carried HTTP 200 rather than 503.</param>
    public static NodeHealthSnapshot From(
        string nodeName,
        RenderNodeHealthV1 health,
        DateTimeOffset pulledAt,
        bool httpOk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(health);

        return new NodeHealthSnapshot(
            nodeName,
            pulledAt,
            httpOk,
            health.Status,
            health.Reasons,
            health.Capacity.MaxConcurrentTasks,
            health.Capacity.ActiveTasks,
            health.Capacity.Accepting,
            health.Gpu.UtilPct,
            health.Gpu.MemUsedMb,
            health.Disk.UsedPct,
            health.Gpu.TempC);
    }
}

/// <summary>
/// The local health reason of a node that did not answer at all (11 README plan §10 open item 4, T26):
/// the 05 README D4 <c>reasons</c> set is only sent by a node that answered, so
/// <c>render_node_health_reasons_total</c> needs one label of its own for "no valid answer".
/// </summary>
public static class LocalNodeHealthReasons
{
    /// <summary>The reason recorded when a pull produced no valid answer (connect error, pin mismatch, timeout, unparseable body).</summary>
    public const string NodeUnreachable = "node_unreachable";
}

/// <summary>
/// The in-memory health cache of the Worker (11 README D13): the poller writes one entry per valid answer,
/// the selector and the node gauges read it. Nothing is persisted — the row's
/// <c>last_heartbeat_at</c> is the durable half of the same fact — so a scrape never touches a node (Q1)
/// and a Worker restart starts the cache empty (the first 30 s are non-dispatchable by design).
/// </summary>
public interface INodeHealthCache
{
    /// <summary>Replaces the node's entry with <paramref name="snapshot"/> (keyed by node name).</summary>
#pragma warning disable CA1716 // The name is the contract of 11.3.3 plan §7; a rename would break the poller, the selector and the gauges.
    void Set(NodeHealthSnapshot snapshot);
#pragma warning restore CA1716

    /// <summary>Reads the node's last snapshot; <c>false</c> when the node has not answered yet.</summary>
    bool TryGet(string nodeName, out NodeHealthSnapshot? snapshot);

    /// <summary>Every cached snapshot, for the observable gauges; bounded by the node count.</summary>
    IReadOnlyCollection<NodeHealthSnapshot> All();
}
