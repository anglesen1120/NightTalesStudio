using System.Diagnostics.Metrics;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// The real-time instruments of the platform (09.8.1-T1 plan §7; exported by 09.9.3-T1):
/// <c>realtime_notify_total{event}</c>, <c>realtime_notify_dropped_total{event}</c>,
/// <c>realtime_listener_reconnects_total</c>, <c>realtime_listener_messages_total{channel}</c>,
/// <c>realtime_listener_deliveries_dropped_total</c>, <c>realtime_listener_deliveries_failed_total</c>
/// and the <c>realtime_connections_active</c> gauge.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton, like every meter: one instrument per process, so a scrape never sees two
/// series for one counter. A test attaches a <see cref="MeterListener"/> to exactly this host's meter
/// instance.
/// </para>
/// <para>
/// The tags carry closed vocabularies only — the wire event name and the channel name — never an id, a
/// ticket or a payload, so a label can never become a high-cardinality or leaking dimension
/// (09 README D17/BD16).
/// </para>
/// <para>
/// <c>realtime_notify_dropped_total</c> is a contract violation and 09.8.1 plan §11 pages on it: an
/// ids-only body cannot exceed 8 KB, so a non-zero value means a producer wrote something it must not.
/// </para>
/// </remarks>
public sealed class RealtimeMetrics : IDisposable
{
    /// <summary>The meter every instrument of the real-time transport publishes on.</summary>
    public const string MeterName = "NightTales.Realtime";

    /// <summary>The counter of notifications written to the <c>realtime</c> channel.</summary>
    public const string NotifyCounterName = "realtime_notify_total";

    /// <summary>The counter of notifications dropped because their body was too large.</summary>
    public const string NotifyDroppedCounterName = "realtime_notify_dropped_total";

    /// <summary>The counter of listener reconnect attempts.</summary>
    public const string ListenerReconnectsCounterName = "realtime_listener_reconnects_total";

    /// <summary>The counter of notifications the listener read, by channel.</summary>
    public const string ListenerMessagesCounterName = "realtime_listener_messages_total";

    /// <summary>The counter of events dropped because the listener's send queue was full.</summary>
    public const string ListenerDeliveriesDroppedCounterName = "realtime_listener_deliveries_dropped_total";

    /// <summary>The counter of events a group send could not deliver (timeout or error).</summary>
    public const string ListenerDeliveriesFailedCounterName = "realtime_listener_deliveries_failed_total";

    /// <summary>The gauge of the connections registered on this instance.</summary>
    public const string ConnectionsActiveGaugeName = "realtime_connections_active";

    /// <summary>The tag carrying the wire event name.</summary>
    public const string EventTagName = "event";

    /// <summary>The tag carrying the PostgreSQL channel name.</summary>
    public const string ChannelTagName = "channel";

    private readonly Counter<long> _listenerMessages;
    private readonly Counter<long> _listenerReconnects;
    private readonly Counter<long> _listenerDeliveriesDropped;
    private readonly Counter<long> _listenerDeliveriesFailed;
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _notify;
    private readonly Counter<long> _notifyDropped;
    private int _connectionsActive;

    /// <summary>The host's meter instance, so a test listens on exactly this one.</summary>
    public Meter Meter => _meter;

    /// <summary>Creates the meter and its instruments.</summary>
    public RealtimeMetrics()
    {
        _notify = _meter.CreateCounter<long>(
            NotifyCounterName,
            unit: "{notification}",
            description: "Notifications written to the realtime channel, by wire event name.");

        _notifyDropped = _meter.CreateCounter<long>(
            NotifyDroppedCounterName,
            unit: "{notification}",
            description: "Notifications dropped because their body exceeded the cap, by wire event name.");

        _listenerReconnects = _meter.CreateCounter<long>(
            ListenerReconnectsCounterName,
            unit: "{attempt}",
            description: "Listener reconnect attempts after a dropped LISTEN connection.");

        _listenerMessages = _meter.CreateCounter<long>(
            ListenerMessagesCounterName,
            unit: "{notification}",
            description: "Notifications the listener read, by channel.");

        _listenerDeliveriesDropped = _meter.CreateCounter<long>(
            ListenerDeliveriesDroppedCounterName,
            unit: "{event}",
            description: "Events dropped because the listener's send queue was full (drop-oldest).");

        _listenerDeliveriesFailed = _meter.CreateCounter<long>(
            ListenerDeliveriesFailedCounterName,
            unit: "{event}",
            description: "Events a group send could not deliver before its timeout or because it threw.");

        _meter.CreateObservableGauge(
            ConnectionsActiveGaugeName,
            () => Volatile.Read(ref _connectionsActive),
            unit: "{connection}",
            description: "Hub connections currently registered on this instance.");
    }

    /// <summary>Counts one notification written to the channel.</summary>
    /// <param name="wireEvent">The wire event name (<c>episode_updated</c>, …).</param>
    public void Notify(string wireEvent) =>
        _notify.Add(1, new KeyValuePair<string, object?>(EventTagName, wireEvent));

    /// <summary>Counts one notification dropped for exceeding the payload cap.</summary>
    /// <param name="wireEvent">The wire event name of the dropped event.</param>
    public void NotifyDropped(string wireEvent) =>
        _notifyDropped.Add(1, new KeyValuePair<string, object?>(EventTagName, wireEvent));

    /// <summary>Counts one reconnect attempt after a dropped listener connection.</summary>
    public void ListenerReconnect() => _listenerReconnects.Add(1);

    /// <summary>Counts one notification the listener read.</summary>
    /// <param name="channel">The PostgreSQL channel it arrived on.</param>
    public void ListenerMessage(string channel) =>
        _listenerMessages.Add(1, new KeyValuePair<string, object?>(ChannelTagName, channel));

    /// <summary>Counts one event dropped because the listener's send queue was full.</summary>
    public void ListenerDeliveryDropped() => _listenerDeliveriesDropped.Add(1);

    /// <summary>Counts one event a group send could not deliver (timeout or error).</summary>
    public void ListenerDeliveryFailed() => _listenerDeliveriesFailed.Add(1);

    /// <summary>Reports how many connections this instance currently holds.</summary>
    /// <param name="count">The registry's size after the change.</param>
    public void RecordActiveConnections(int count) => Volatile.Write(ref _connectionsActive, count);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
