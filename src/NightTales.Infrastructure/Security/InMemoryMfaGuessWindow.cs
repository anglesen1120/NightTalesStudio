namespace NightTales.Infrastructure.Security;

/// <summary>
/// The in-process implementation of <see cref="IMfaGuessWindow"/> (WT35): a bounded, thread-safe
/// per-user queue of the wrong codes of the last fifteen minutes.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton, because the window is one fact per user for the whole process — a scoped
/// instance would reset it on every request. Every entry is pruned against the caller's clock reading
/// (<c>IClock</c> is passed in, never read here), so a test can move time and see the window close.
/// </para>
/// <para>
/// Both dimensions are bounded. Each user's queue keeps only the newest <see cref="Threshold"/>
/// timestamps — enough to decide "blocked" — and the dictionary is held at
/// <see cref="MaxTrackedUsers"/> by evicting expired windows first and then the least-recently-wrong
/// entries that are <em>not</em> currently blocked, in batches of <see cref="EvictionBatch"/> so the scan
/// is amortized over many inserts instead of running on every one. An active lockout is evicted only when
/// every tracked entry is active (a genuinely saturated window); a caller never sees an exception, only
/// "not blocked".
/// </para>
/// </remarks>
public sealed class InMemoryMfaGuessWindow : IMfaGuessWindow
{
    /// <summary>The number of wrong codes inside the window that blocks the user (WT35: 10).</summary>
    public const int Threshold = 10;

    /// <summary>How long a wrong code counts (WT35: 15 minutes).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The wait a blocked client is told (WT35 fixes <c>Retry-After</c> at 900 seconds, the window's
    /// length, not the remainder of it).
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>The same value as the integer the problem's <c>Retry-After</c> carries.</summary>
    public const int RetryAfterSeconds = 900;

    /// <summary>The user ids the process tracks before it evicts entries.</summary>
    public const int MaxTrackedUsers = 10_000;

    /// <summary>
    /// How many entries one eviction sweep frees, so the O(n) scan runs once per this many inserts rather
    /// than on every insert while the dictionary is at its cap.
    /// </summary>
    public const int EvictionBatch = 1_000;

    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _wrongCodes = [];

    /// <summary>The users whose trip has already been reported, so WT35's row is written once.</summary>
    private readonly HashSet<Guid> _reported = [];

    private readonly Lock _gate = new();

    /// <summary>The number of users the window currently tracks (diagnostics; never above the cap).</summary>
    public int TrackedUsers
    {
        get
        {
            lock (_gate)
            {
                return _wrongCodes.Count;
            }
        }
    }

    /// <summary>
    /// The total number of queued wrong codes (diagnostics; every user's queue is bounded by
    /// <see cref="Threshold"/>, so this is at most <c>Threshold × </c><see cref="TrackedUsers"/>).
    /// </summary>
    public int TrackedCodes
    {
        get
        {
            lock (_gate)
            {
                var total = 0;
                foreach (var times in _wrongCodes.Values)
                {
                    total += times.Count;
                }

                return total;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan? BlockedFor(Guid userId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_wrongCodes.TryGetValue(userId, out var times))
            {
                return null;
            }

            Prune(times, now);

            if (times.Count == 0)
            {
                Forget(userId);
                return null;
            }

            return times.Count >= Threshold ? RetryAfter : null;
        }
    }

    /// <inheritdoc />
    public bool TryClaimTripReport(Guid userId)
    {
        lock (_gate)
        {
            return _reported.Add(userId);
        }
    }

    /// <inheritdoc />
    public bool RegisterWrongCode(Guid userId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_wrongCodes.TryGetValue(userId, out var times))
            {
                if (_wrongCodes.Count >= MaxTrackedUsers)
                {
                    Evict(now);
                }

                times = new Queue<DateTimeOffset>(Threshold);
                _wrongCodes[userId] = times;
            }

            Prune(times, now);

            if (times.Count == 0)
            {
                // The previous window is over: the trip it may have reported is forgotten with it, so a
                // later trip writes its own row (WT35's row marks a window, not a user forever).
                Forget(userId);
                times = new Queue<DateTimeOffset>(Threshold);
                _wrongCodes[userId] = times;
            }

            times.Enqueue(now);

            // Only the newest Threshold timestamps can decide "blocked": keeping more costs memory without
            // changing any answer.
            if (times.Count > Threshold)
            {
                _ = times.Dequeue();
            }

            // Exactly the crossing call answers true, so the single audit row of WT35 is written once.
            return times.Count == Threshold;
        }
    }

    /// <summary>Drops the entries of one user that fell out of the window.</summary>
    private static void Prune(Queue<DateTimeOffset> times, DateTimeOffset now)
    {
        var oldest = now - Window;
        while (times.Count > 0 && times.Peek() <= oldest)
        {
            _ = times.Dequeue();
        }
    }

    /// <summary>
    /// Restores the user cap without silently dropping active lockouts in ordinary operation.
    /// </summary>
    /// <remarks>
    /// Expired windows go first. Still over the cap, the least-recently-wrong <em>unblocked</em> entries go
    /// next; a currently blocked entry is chosen only when every remaining entry is blocked (a genuinely
    /// saturated window), which is the only way to keep the hard bound. The sweep frees
    /// <see cref="EvictionBatch"/> entries so it runs once per that many inserts, not once per insert.
    /// </remarks>
    private void Evict(DateTimeOffset now)
    {
        // Prune every queue and drop the windows that have fully expired.
        foreach (var userId in _wrongCodes.Keys.ToList())
        {
            var times = _wrongCodes[userId];
            Prune(times, now);
            if (times.Count == 0)
            {
                Forget(userId);
            }
        }

        var excess = _wrongCodes.Count - (MaxTrackedUsers - EvictionBatch);
        if (excess <= 0)
        {
            return;
        }

        // Unblocked first (false < true), then the oldest activity: the entries least likely to be a live
        // lockout go first.
        var victims = _wrongCodes
            .Select(entry => (
                UserId: entry.Key,
                Newest: entry.Value.Max(),
                Blocked: entry.Value.Count >= Threshold))
            .OrderBy(entry => entry.Blocked)
            .ThenBy(entry => entry.Newest)
            .Take(excess);

        foreach (var victim in victims)
        {
            Forget(victim.UserId);
        }
    }

    /// <summary>Forgets one user completely, so the next window starts unblocked and unreported.</summary>
    private void Forget(Guid userId)
    {
        _ = _wrongCodes.Remove(userId);
        _ = _reported.Remove(userId);
    }
}
