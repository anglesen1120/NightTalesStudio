using NightTales.Application.Abstractions.Realtime;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// The real-time transport configuration of a host, bound from the <c>Realtime</c> section
/// (09.8.1 plan §7): the listener's heartbeat, backoff and delivery bounds of 09.8.1-T1, plus the
/// ticket keys of 09.8.1-T2.
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Realtime";

    /// <summary>The listener's heartbeat when <see cref="HeartbeatSeconds"/> is not set.</summary>
    public const int DefaultHeartbeatSeconds = 30;

    /// <summary>The backstop sweep interval when <see cref="RevalidateSeconds"/> is not set.</summary>
    public const int DefaultRevalidateSeconds = 60;

    /// <summary>
    /// The reconnect schedule of 09 README BD11 when <see cref="ListenerBackoffSeconds"/> is not set:
    /// 1, 2, 4, 8, 16, 30 seconds, then 30 seconds forever.
    /// </summary>
    private static readonly int[] DefaultListenerBackoffSeconds = [1, 2, 4, 8, 16, 30];

    /// <summary>
    /// The exclusive cap of a NOTIFY body in UTF-8 bytes (<c>Realtime:MaxPayloadBytes</c>); a body of this
    /// many bytes or more is dropped and counted, never truncated (BD11/BD16). PostgreSQL refuses a
    /// payload as long as its own 8,000-byte limit, so the default — and the highest value
    /// <c>AddRealtime</c> accepts — is <see cref="RealtimePayload.MaxBytes"/>, whose last deliverable body
    /// is 7,999 bytes.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = RealtimePayload.MaxBytes;

    /// <summary>
    /// How long the listener waits on its connection before it treats the wait as a heartbeat
    /// (<c>Realtime:HeartbeatSeconds</c>); the wait itself is the loop's pause, so there is no busy
    /// loop.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = DefaultHeartbeatSeconds;

    /// <summary>
    /// How often the listener sweeps connections against <c>Hubs:MaxConnectionMinutes</c>
    /// (<c>Realtime:RevalidateSeconds</c>). No stamp or status cache is read by that sweep
    /// ([RULED — README §B10 WT37]).
    /// </summary>
    public int RevalidateSeconds { get; set; } = DefaultRevalidateSeconds;

    /// <summary>
    /// The reconnect schedule in seconds (<c>Realtime:ListenerBackoffSeconds:0</c>, <c>:1</c>, …). The
    /// last entry repeats, so an outage longer than the schedule keeps retrying at that interval.
    /// </summary>
    public List<int> ListenerBackoffSeconds { get; set; } = [.. DefaultListenerBackoffSeconds];

    /// <summary>The listener's outbound queue depth when <see cref="DeliveryQueueCapacity"/> is not set.</summary>
    public const int DefaultDeliveryQueueCapacity = 1024;

    /// <summary>The per-send bound when <see cref="DeliveryTimeoutSeconds"/> is not set.</summary>
    public const int DefaultDeliveryTimeoutSeconds = 10;

    /// <summary>
    /// How many <c>realtime</c> events may wait for the group sender
    /// (<c>Realtime:DeliveryQueueCapacity</c>). The reading loop never blocks on a socket: a full queue
    /// drops the oldest event and counts it, and the queue's single consumer owns every send, so a socket
    /// under back-pressure holds neither the <c>LISTEN</c> session nor the <c>security</c> abort.
    /// </summary>
    public int DeliveryQueueCapacity { get; set; } = DefaultDeliveryQueueCapacity;

    /// <summary>
    /// How long one group send may take before it is abandoned
    /// (<c>Realtime:DeliveryTimeoutSeconds</c>): a socket that never accepts the write must not hold the
    /// single delivery consumer — and every event behind it — any longer.
    /// </summary>
    public int DeliveryTimeoutSeconds { get; set; } = DefaultDeliveryTimeoutSeconds;

    /// <summary>The ticket lifetime when <see cref="TicketTtlSeconds"/> is not set (BD11: 60 seconds).</summary>
    public const int DefaultTicketTtlSeconds = 60;

    /// <summary>The ticket sweep's retention when <see cref="TicketCleanupRetentionHours"/> is not set.</summary>
    public const int DefaultTicketCleanupRetentionHours = 24;

    /// <summary>The ticket sweep's batch when <see cref="TicketCleanupBatchSize"/> is not set.</summary>
    public const int DefaultTicketCleanupBatchSize = 1_000;

    /// <summary>
    /// How long an issued hub ticket stays claimable (<c>Realtime:TicketTtlSeconds</c>, BD11): the
    /// instant the row is written plus this value is its <c>expires_at</c>.
    /// </summary>
    public int TicketTtlSeconds { get; set; } = DefaultTicketTtlSeconds;

    /// <summary>
    /// How long after its expiry a ticket row is kept before <c>HubTicketCleanupJob</c> may delete it
    /// (<c>Realtime:TicketCleanupRetentionHours</c>); the window is what makes a failed claim
    /// diagnosable instead of invisible.
    /// </summary>
    public int TicketCleanupRetentionHours { get; set; } = DefaultTicketCleanupRetentionHours;

    /// <summary>
    /// The rows one <c>HubTicketCleanupJob</c> run may delete (<c>Realtime:TicketCleanupBatchSize</c>),
    /// so a sweep's transaction stays short.
    /// </summary>
    public int TicketCleanupBatchSize { get; set; } = DefaultTicketCleanupBatchSize;

    /// <summary>The ticket lifetime as a duration.</summary>
    public TimeSpan TicketTtl => TimeSpan.FromSeconds(TicketTtlSeconds);

    /// <summary>The ticket sweep's retention as a duration.</summary>
    public TimeSpan TicketCleanupRetention => TimeSpan.FromHours(TicketCleanupRetentionHours);

    /// <summary>The heartbeat as a duration.</summary>
    public TimeSpan Heartbeat => TimeSpan.FromSeconds(HeartbeatSeconds);

    /// <summary>The backstop sweep interval as a duration.</summary>
    public TimeSpan Revalidate => TimeSpan.FromSeconds(RevalidateSeconds);

    /// <summary>The per-send bound as a duration.</summary>
    public TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(DeliveryTimeoutSeconds);

    /// <summary>
    /// How long to wait before reconnect attempt <paramref name="attempt"/> (0-based); the schedule's
    /// last entry repeats.
    /// </summary>
    /// <param name="attempt">How many consecutive attempts have already failed.</param>
    public TimeSpan BackoffFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        // AddRealtime validates the schedule on start, so this fallback only serves a hand-built
        // options object (a test): it must still reconnect, just without the configured ramp.
        var schedule = ListenerBackoffSeconds is { Count: > 0 } ? ListenerBackoffSeconds : [.. DefaultListenerBackoffSeconds];

        return TimeSpan.FromSeconds(schedule[Math.Min(attempt, schedule.Count - 1)]);
    }
}
