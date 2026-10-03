using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Rendering;

/// <summary>
/// Picks the render node of the next dispatch (11 README D13, 11.3.3-T2). The decision reads three facts and
/// calls no node: the active <c>worker_nodes</c> rows, the poller's <see cref="INodeHealthCache"/>, and the
/// derived in-flight count of <see cref="INodeLoadReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// Dispatchable = <c>state = active</c> ∧ the snapshot is at most <see cref="RenderNodesOptions.HealthStaleSeconds"/>
/// old ∧ HTTP 200 ∧ <c>accepting</c> ∧ <c>min(capacity, max_concurrent_tasks) − in_flight &gt; 0</c>. A node without
/// a cache entry is not dispatchable, so the Worker's first poll interval dispatches nothing by design.
/// </para>
/// <para>
/// Ordering is free slots descending, then <c>name</c> ascending, <em>ordinal</em> and applied in memory:
/// the node set is bounded (≤ a handful of rows) and PostgreSQL's collation would order differently from the
/// documented tie-break. Two dispatchers therefore agree without coordination. No reservation is made; the
/// <c>submitted</c> row inserted by 09.6.3-T1 consumes the slot (D13 fixes no reservation).
/// </para>
/// <para>
/// The only database work is the <c>worker_nodes</c> read and the single grouped in-flight count of
/// <see cref="INodeLoadReader"/>, never a per-node round trip.
/// </para>
/// </remarks>
public sealed partial class NodeSelector(
    IReadDb readDb,
    INodeHealthCache cache,
    INodeLoadReader loadReader,
    IClock clock,
    RenderNodesOptions settings,
    NodeMetrics metrics,
    NodeDispatchLogGate logGate,
    ILogger<NodeSelector> logger) : INodeSelector
{
    private const string NoCandidateDescription = "no render node with a free slot";

    /// <inheritdoc />
    public async Task<ErrorOr<WorkerNode>> PickAsync(CancellationToken cancellationToken)
    {
        var staleAfter = TimeSpan.FromSeconds(settings.HealthStaleSeconds);

        var nodes = await readDb.Query<WorkerNode>()
            .Where(node => node.State == WorkerNodeState.Active)
            .ToListAsync(cancellationToken);

        var inFlight = nodes.Count == 0
            ? new Dictionary<Guid, int>()
            : await loadReader.InFlightByNodeAsync(cancellationToken);

        // Freshness is judged against the instant the reads finished, not the one before them: a slow
        // database must not let a snapshot that aged out during the reads pass as fresh.
        var now = clock.UtcNow;
        var reasons = new DispatchReasons();
        var candidates = new List<(WorkerNode Node, int Free)>(nodes.Count);

        foreach (var node in nodes)
        {
            var running = inFlight.GetValueOrDefault(node.Id);
            metrics.RecordLoad(node.Name, node.Capacity, running, active: true);

            if (!cache.TryGet(node.Name, out var snapshot) || snapshot is null || now - snapshot.PulledAt > staleAfter)
            {
                reasons.StaleHealth++;
                continue;
            }

            if (!snapshot.HttpOk || !snapshot.Accepting)
            {
                reasons.NotAccepting++;
                continue;
            }

            var free = NodeMetrics.FreeSlots(node.Capacity, snapshot.MaxConcurrentTasks, running);
            if (free <= 0)
            {
                reasons.NoFreeSlot++;
                continue;
            }

            candidates.Add((node, free));
        }

        if (candidates.Count > 0)
        {
            return candidates
                .OrderByDescending(candidate => candidate.Free)
                .ThenBy(candidate => candidate.Node.Name, StringComparer.Ordinal)
                .First()
                .Node;
        }

        metrics.RecordDispatchUnavailable();

        if (logGate.TryClaim(now, TimeSpan.FromSeconds(settings.HealthPollSeconds)))
        {
            LogNoCandidate(
                nodes.Count,
                reasons.StaleHealth,
                reasons.NotAccepting,
                reasons.NoFreeSlot);
        }

        return Error.Failure(ErrorCodes.RenderWorkerUnavailable, NoCandidateDescription);
    }

    [LoggerMessage(
        EventId = 3320,
        Level = LogLevel.Warning,
        Message = "render.dispatch_unavailable activeNodes={ActiveNodes} staleHealth={StaleHealth} notAccepting={NotAccepting} noFreeSlot={NoFreeSlot}")]
    private partial void LogNoCandidate(int activeNodes, int staleHealth, int notAccepting, int noFreeSlot);

    /// <summary>The per-reason histogram of the warning (logging only, never a metric label: bounded cardinality).</summary>
    private sealed class DispatchReasons
    {
        public int StaleHealth { get; set; }

        public int NotAccepting { get; set; }

        public int NoFreeSlot { get; set; }
    }
}
