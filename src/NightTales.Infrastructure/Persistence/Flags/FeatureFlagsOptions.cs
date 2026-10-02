namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// The configuration of the feature-flag read service (09.9.2 plan §7, 09.9.2-T2 §Design): how long a
/// loaded flag row may be served, and how the cross-instance invalidator reconnects.
/// </summary>
/// <remarks>
/// <c>FeatureFlags:CacheSeconds</c> is the one key the plan fixes (default <b>30</b>); the heartbeat and
/// the reconnect schedule carry the same shape as 09 README BD11's listener and are this mechanism's own
/// [INFERENCE] keys, so an operator can slow a flapping reconnect without a redeploy.
/// </remarks>
public sealed class FeatureFlagsOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "FeatureFlags";

    /// <summary>The cache window of 09 README BD13 when <see cref="CacheSeconds"/> is not set.</summary>
    public const int DefaultCacheSeconds = 30;

    /// <summary>The invalidator's heartbeat when <see cref="HeartbeatSeconds"/> is not set.</summary>
    public const int DefaultHeartbeatSeconds = 30;

    /// <summary>
    /// The reconnect schedule when <see cref="ListenerBackoffSeconds"/> is not set: 1, 2, 4, 8, 16 and
    /// 30 seconds, then 30 seconds forever — the same ramp as BD11's real-time listener.
    /// </summary>
    private static readonly int[] DefaultListenerBackoffSeconds = [1, 2, 4, 8, 16, 30];

    /// <summary>
    /// How long one loaded value (a row's state, or the confirmed absence of one) may be served before it
    /// is read again. The window is the hard bound on staleness when a change notification is lost.
    /// </summary>
    public int CacheSeconds { get; set; } = DefaultCacheSeconds;

    /// <summary>
    /// How long the invalidator waits on its <c>LISTEN</c> session before treating the wait as a
    /// heartbeat; the wait itself is the loop's pause, so there is no busy loop.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = DefaultHeartbeatSeconds;

    /// <summary>
    /// The reconnect schedule in seconds (<c>FeatureFlags:ListenerBackoffSeconds:0</c>, <c>:1</c>, …). The
    /// last entry repeats, so an outage longer than the schedule keeps retrying at that interval.
    /// </summary>
    public List<int> ListenerBackoffSeconds { get; set; } = [.. DefaultListenerBackoffSeconds];

    /// <summary>The cache window as a duration.</summary>
    public TimeSpan CacheWindow => TimeSpan.FromSeconds(CacheSeconds);

    /// <summary>The pause before reconnect attempt number <paramref name="attempt"/>.</summary>
    /// <param name="attempt">The zero-based attempt counter; the schedule's last entry repeats.</param>
    /// <exception cref="ArgumentOutOfRangeException">The attempt is negative.</exception>
    public TimeSpan BackoffFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        // The composition root validates the schedule on start, so this fallback only serves a hand-built
        // options object (a test): it must still reconnect, just without the configured ramp.
        var schedule = ListenerBackoffSeconds is { Count: > 0 }
            ? ListenerBackoffSeconds
            : [.. DefaultListenerBackoffSeconds];

        return TimeSpan.FromSeconds(schedule[Math.Min(attempt, schedule.Count - 1)]);
    }
}
