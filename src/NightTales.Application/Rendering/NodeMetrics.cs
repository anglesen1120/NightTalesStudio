using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

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

    /// <summary>The tag carrying the node's name.</summary>
    public const string NodeTagName = "node";

    /// <summary>The tag carrying a health reason (the wire reason, or <c>node_unreachable</c>).</summary>
    public const string ReasonTagName = "reason";

    private readonly ConcurrentDictionary<string, int> _up = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _certExpiryDays = new(StringComparer.Ordinal);
    private readonly Counter<long> _healthReasons;
    private readonly Counter<long> _drift;
    private readonly Meter _meter = new(MeterName);

    /// <summary>Creates the meter and its four instruments.</summary>
    public NodeMetrics()
    {
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

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> NodeTag(string node) => new(NodeTagName, node);
}
