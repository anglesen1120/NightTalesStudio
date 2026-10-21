using System.Collections.Concurrent;
using System.Net;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Counts rejected authentication attempts per source address over a sliding 60-second window (05 D15 S2,
/// §10 T68): once <c>AuthFailuresPerMinute</c> failures fall inside the window, the next failing request is
/// <c>429 adapter_auth_throttled</c> with <c>Retry-After: 60</c>. A valid key is never throttled — the core
/// authenticates first and only counts failures (the throttle rejects further failures, not traffic).
/// </summary>
/// <remarks>
/// Only the newest <c>AuthFailuresPerMinute</c> timestamps per source are kept, and the number of tracked
/// sources is hard-bounded by <see cref="MaxTrackedSources"/>: an admission that would exceed the cap
/// evicts the least-recently-active (or expired) windows, so a flood of distinct addresses cannot grow the
/// state or turn every failure into a full scan. <see cref="TimeProvider"/> is injected so the window is
/// testable without waiting a minute. <see cref="RecordFailedAttempt"/> prunes, tests the threshold and
/// records as one atomic step on the source's window.
/// </remarks>
public sealed class AuthFailureThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const int MaxTrackedSources = 4096;

    /// <summary>How many free slots an eviction leaves behind, so the next admissions do not rescan.</summary>
    private const int EvictionLowWater = MaxTrackedSources - (MaxTrackedSources / 8);

    private readonly int _failuresPerMinute;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<IPAddress, FailureWindow> _windows = new();

    /// <param name="failuresPerMinute">Failures allowed inside one minute before requests are throttled.</param>
    /// <param name="timeProvider">The clock the window is measured on.</param>
    public AuthFailureThrottle(int failuresPerMinute, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failuresPerMinute);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _failuresPerMinute = failuresPerMinute;
        _timeProvider = timeProvider;
    }

    /// <summary>The number of tracked source windows; never more than <see cref="MaxTrackedSources"/> after a call.</summary>
    internal int TrackedSourceCount => _windows.Count;

    /// <summary>
    /// Records one rejected authentication attempt from this source and answers whether the source is now
    /// throttled. Pruning, the threshold test and the record happen under one lock, so a burst of concurrent
    /// failures from one address crosses the threshold exactly once, and a throttled attempt is not recorded —
    /// the 429 therefore never extends the window (11.3.5-T1 Failure handling).
    /// </summary>
    public bool RecordFailedAttempt(IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var throttled = WindowFor(source).RecordFailedAttempt(_timeProvider.GetUtcNow());
        EnforceBound();
        return throttled;
    }

    /// <summary>
    /// The source address of a request is the TCP peer; <c>X-Forwarded-For</c> is ignored (D15 S2). An
    /// IPv4-mapped IPv6 address is normalised to IPv4 so one client cannot occupy two buckets.
    /// </summary>
    internal static IPAddress Normalize(IPAddress? source) => source switch
    {
        null => IPAddress.None,
        { IsIPv4MappedToIPv6: true } mapped => mapped.MapToIPv4(),
        _ => source,
    };

    private FailureWindow WindowFor(IPAddress source) =>
        _windows.GetOrAdd(source, _ => new FailureWindow(_failuresPerMinute));

    /// <summary>
    /// Keeps <see cref="MaxTrackedSources"/> a real bound. Called only after a new admission pushed the count
    /// over the cap; it evicts the oldest (expired first) windows down to a low-water mark, so the scan is
    /// amortised and never runs per failure while the table is below the cap.
    /// </summary>
    private void EnforceBound()
    {
        if (_windows.Count <= MaxTrackedSources)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var candidates = new List<(IPAddress Source, DateTimeOffset Oldest)>(_windows.Count);
        foreach (var (source, window) in _windows)
        {
            candidates.Add((source, window.OldestFailure(now)));
        }

        candidates.Sort((left, right) => left.Oldest.CompareTo(right.Oldest));
        foreach (var (source, _) in candidates)
        {
            if (_windows.Count <= EvictionLowWater)
            {
                return;
            }

            _windows.TryRemove(source, out _);
        }
    }

    /// <summary>The newest failure timestamps of one source, oldest first, capped at the configured limit.</summary>
    private sealed class FailureWindow(int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _failures = new(capacity);

        /// <summary>Prunes, then either records this failure or reports the source as already throttled.</summary>
        internal bool RecordFailedAttempt(DateTimeOffset now)
        {
            lock (_gate)
            {
                Prune(now);
                if (_failures.Count >= capacity)
                {
                    return true;
                }

                _failures.Enqueue(now);
                return false;
            }
        }

        /// <summary>The oldest in-window failure, or <see cref="DateTimeOffset.MinValue"/> when the window is empty.</summary>
        internal DateTimeOffset OldestFailure(DateTimeOffset now)
        {
            lock (_gate)
            {
                Prune(now);
                return _failures.Count == 0 ? DateTimeOffset.MinValue : _failures.Peek();
            }
        }

        private void Prune(DateTimeOffset now)
        {
            var oldest = now - Window;
            while (_failures.Count > 0 && _failures.Peek() <= oldest)
            {
                _failures.Dequeue();
            }
        }
    }
}
