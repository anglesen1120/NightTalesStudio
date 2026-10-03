using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Common;

namespace NightTales.Application.Rendering;

/// <summary>
/// The render-node instruments of 11 README D13 (09.9.3 conventions), owned by the Worker's health poller
/// and the connection factory. Cardinality is bounded by the node count, and the only tag values are the
/// node's name — never a URL, a key or a body.
/// </summary>
/// <remarks>
/// <para>
/// This task emits the poller tick's <c>render_node_up</c> and <c>render_node_health_reasons_total</c>, the
/// drift counter <c>render_node_active_tasks_drift_total</c>, and the factory's
/// <c>render_node_cert_expiry_days</c> [RULED — README §10 T57] (D20 H12). 11.3.3-T2 adds the observable
/// gauges that read <c>INodeHealthCache</c> to the same <see cref="Meter"/>, so a scrape never calls a node
/// (Q1) and no second meter is created for the same family.
/// </para>
/// <para>
/// The gauges are event-driven: <c>render_node_up</c> and <c>render_node_cert_expiry_days</c> report the
/// last value the writer recorded, so the instruments keep working between scrapes and after a node falls
/// silent. A registered singleton per host, like <c>AuthMetrics</c>/<c>OutboxMetrics</c>.
/// </para>
/// </remarks>
public sealed class NodeMetrics : IDisposable
{
    /// <summary>The meter every node instrument lives on.</summary>
    public const string MeterName = "NightTales.RenderNode";

    /// <summary>1 when the node gave a valid answer this tick, 0 otherwise (D13).</summary>
    public const string UpInstrumentName = "render_node_up";

    /// <summary>Health reasons seen, one increment per reason per tick (D13).</summary>
    public const string HealthReasonsInstrumentName = "render_node_health_reasons_total";

    /// <summary>Consecutive-report mismatches between derived and reported active tasks (D13, plan §10 item 6).</summary>
    public const string ActiveTasksDriftInstrumentName = "render_node_active_tasks_drift_total";

    /// <summary>Whole days until the pinned certificate expires, from the presented certificate (D20 H12).</summary>
    public const string CertExpiryDaysInstrumentName = "render_node_cert_expiry_days";

    /// <summary>Free dispatch slots per node: <c>min(capacity, max_concurrent_tasks) − in_flight</c>, never negative (D13).</summary>
    public const string FreeSlotsInstrumentName = "render_node_free_slots";

    /// <summary>Picks that found no dispatchable node (D13, D17 alert).</summary>
    public const string DispatchUnavailableInstrumentName = "render_dispatch_unavailable_total";

    /// <summary>The node's cached GPU utilisation in percent.</summary>
    public const string GpuUtilInstrumentName = "render_node_gpu_util_pct";

    /// <summary>The node's cached GPU memory in use, in bytes (the wire carries MiB).</summary>
    public const string GpuMemUsedInstrumentName = "render_node_gpu_mem_used_bytes";

    /// <summary>The node's cached GPU temperature in degrees Celsius (T28).</summary>
    public const string GpuTempInstrumentName = "render_node_gpu_temp_celsius";

    /// <summary>The node's cached task-dir disk use in percent.</summary>
    public const string DiskUsedInstrumentName = "render_node_disk_used_pct";

    /// <summary>The tag carrying the node's name.</summary>
    public const string NodeTagName = "node";

    /// <summary>The tag carrying a health reason (the wire reason, or <c>node_unreachable</c>).</summary>
    public const string ReasonTagName = "reason";

    private readonly ConcurrentDictionary<string, int> _up = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _certExpiryDays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeLoad> _load = new(StringComparer.Ordinal);
    private readonly Counter<long> _dispatchUnavailable;
    private readonly IClock? _clock;
    private readonly TimeSpan _staleAfter;
    private readonly Counter<long> _healthReasons;
    private readonly Counter<long> _drift;
    private readonly Meter _meter = new(MeterName);

    /// <summary>Creates the meter and its instruments.</summary>
    /// <param name="healthCache">
    /// The cache the cache-backed gauges read (free slots, GPU, disk); a scrape never calls a node (Q1).
    /// <c>null</c> registers only the event-driven instruments, for hosts and tests that have no cache.
    /// </param>
    /// <param name="clock">The clock the free-slots gauge ages snapshots with; <c>null</c> skips the freshness check.</param>
    /// <param name="settings">Supplies <see cref="RenderNodesOptions.HealthStaleSeconds"/>; <c>null</c> means the default.</param>
    public NodeMetrics(INodeHealthCache? healthCache = null, IClock? clock = null, RenderNodesOptions? settings = null)
    {
        _clock = clock;
        _staleAfter = TimeSpan.FromSeconds((settings ?? new RenderNodesOptions()).HealthStaleSeconds);

        _meter.CreateObservableGauge(
            UpInstrumentName,
            () => _up.Select(pair => new Measurement<int>(pair.Value, NodeTag(pair.Key))),
            unit: "1",
            description: "1 when the node answered with a valid render-node-health.v1 body this tick, 0 otherwise.");

        _healthReasons = _meter.CreateCounter<long>(
            HealthReasonsInstrumentName,
            unit: "{reason}",
            description: "Health reasons reported by a node, one increment per reason per tick.");

        _drift = _meter.CreateCounter<long>(
            ActiveTasksDriftInstrumentName,
            unit: "{mismatch}",
            description: "Runs of consecutive drift between the derived and the reported active-task count.");

        _meter.CreateObservableGauge(
            CertExpiryDaysInstrumentName,
            () => _certExpiryDays.Select(pair => new Measurement<double>(pair.Value, NodeTag(pair.Key))),
            unit: "d",
            description: "Whole days until the pinned certificate's NotAfter, from the presented certificate.");

        _dispatchUnavailable = _meter.CreateCounter<long>(
            DispatchUnavailableInstrumentName,
            unit: "{pick}",
            description: "Picks that returned render_worker_unavailable because no node was dispatchable.");

        if (healthCache is null)
        {
            return;
        }

        _meter.CreateObservableGauge(
            FreeSlotsInstrumentName,
            () => FreeSlotMeasurements(healthCache),
            unit: "{slot}",
            description: "min(capacity, max_concurrent_tasks) - in_flight per node, never negative; 0 for a node without a fresh, accepting health answer.");

        _meter.CreateObservableGauge(
            GpuUtilInstrumentName,
            () => Cached(healthCache, snapshot => snapshot.GpuUtilPct),
            unit: "%",
            description: "GPU utilisation from the node's last cached health answer.");

        _meter.CreateObservableGauge(
            GpuMemUsedInstrumentName,
            () => Cached(healthCache, snapshot => snapshot.GpuMemUsedMb is { } mib ? mib * 1024.0 * 1024.0 : null),
            unit: "By",
            description: "GPU memory in use from the node's last cached health answer.");

        _meter.CreateObservableGauge(
            GpuTempInstrumentName,
            () => Cached(healthCache, snapshot => snapshot.GpuTempC),
            unit: "Cel",
            description: "GPU temperature from the node's last cached health answer.");

        _meter.CreateObservableGauge(
            DiskUsedInstrumentName,
            () => Cached(healthCache, snapshot => snapshot.DiskUsedPct),
            unit: "%",
            description: "Task-dir disk use from the node's last cached health answer.");
    }

    /// <summary>The host's meter; a test attaches a <see cref="MeterListener"/> to exactly this instance.</summary>
    public Meter Meter => _meter;

    /// <summary>Records whether the node answered validly in this tick.</summary>
    /// <param name="node">The node's name.</param>
    /// <param name="up">Whether a valid answer arrived.</param>
    public void RecordUp(string node, bool up)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        _up[node] = up ? 1 : 0;
    }

    /// <summary>Counts one health reason of one node.</summary>
    /// <param name="node">The node's name.</param>
    /// <param name="reason">The wire reason, or <see cref="Abstractions.Rendering.LocalNodeHealthReasons.NodeUnreachable"/>.</param>
    public void RecordHealthReason(string node, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        _healthReasons.Add(1, NodeTag(node), new KeyValuePair<string, object?>(ReasonTagName, reason));
    }

    /// <summary>Counts the drift run that just reached the alert threshold.</summary>
    /// <param name="node">The node's name.</param>
    public void RecordActiveTasksDrift(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        _drift.Add(1, NodeTag(node));
    }

    /// <summary>Records the whole days left on the certificate the node presented during a TLS handshake.</summary>
    /// <param name="node">The node's name.</param>
    /// <param name="days">Whole days until <c>NotAfter</c>; negative is impossible, because the factory refuses an expired certificate.</param>
    public void RecordCertExpiryDays(string node, double days)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        _certExpiryDays[node] = days;
    }

    /// <summary>
    /// Records a node's slot ceiling and derived in-flight count, which the free-slots gauge combines with
    /// the cached health. Written by every pick and every poll tick; a scrape only reads it (no DB, no HTTP).
    /// </summary>
    /// <param name="node">The node's name.</param>
    /// <param name="capacity">The operator's ceiling, <c>worker_nodes.capacity</c>.</param>
    /// <param name="inFlight">The derived count of <c>submitted</c> + <c>running</c> renders.</param>
    /// <param name="active">Whether the registry row is <c>active</c>; a draining or down node is never dispatchable, whatever its health says.</param>
    public void RecordLoad(string node, int capacity, int inFlight, bool active)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        _load[node] = new NodeLoad(capacity, inFlight, active);
    }

    /// <summary>Counts one pick that returned <c>render_worker_unavailable</c>.</summary>
    public void RecordDispatchUnavailable() => _dispatchUnavailable.Add(1);

    /// <summary>The free dispatch slots of a node: <c>min(capacity, max_concurrent_tasks) − in_flight</c>, floored at 0 (D13).</summary>
    /// <param name="capacity">The operator's ceiling.</param>
    /// <param name="maxConcurrentTasks">What the node's adapter reports it accepts.</param>
    /// <param name="inFlight">The derived in-flight count.</param>
    public static int FreeSlots(int capacity, int maxConcurrentTasks, int inFlight) =>
        Math.Max(0, Math.Min(capacity, maxConcurrentTasks) - inFlight);

    private IEnumerable<Measurement<int>> FreeSlotMeasurements(INodeHealthCache cache)
    {
        // The dispatch predicate (NodeSelector): state = active ∧ fresh ∧ HTTP 200 ∧ accepting. A node failing it
        // offers no slot, so the gauge never advertises the capacity a stale or refusing node had before it
        // went quiet. Reads the clock and the cache only — a scrape never touches the database or a node.
        var now = _clock?.UtcNow;

        foreach (var (node, load) in _load)
        {
            var free = load.Active
                && cache.TryGet(node, out var snapshot)
                && snapshot is not null
                && snapshot.HttpOk
                && snapshot.Accepting
                && (now is not { } instant || instant - snapshot.PulledAt <= _staleAfter)
                    ? FreeSlots(load.Capacity, snapshot.MaxConcurrentTasks, load.InFlight)
                    : 0;

            yield return new Measurement<int>(free, NodeTag(node));
        }
    }

    private static IEnumerable<Measurement<double>> Cached(
        INodeHealthCache cache,
        Func<NodeHealthSnapshot, double?> select)
    {
        foreach (var snapshot in cache.All())
        {
            if (select(snapshot) is { } value)
            {
                yield return new Measurement<double>(value, NodeTag(snapshot.NodeName));
            }
        }
    }

    private readonly record struct NodeLoad(int Capacity, int InFlight, bool Active);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> NodeTag(string node) => new(NodeTagName, node);
}
