using Microsoft.Extensions.Logging;

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
/// <see cref="MaxTrackedUsers"/> by evicting fully-expired windows first and then the least-recently-wrong
/// entries that are <em>not</em> currently blocked, in batches of <see cref="EvictionBatch"/> so the scan
/// is amortized over many inserts instead of running on every one.
/// </para>
/// <para>
/// <b>An active lockout is never evicted.</b> When the cap cannot be reclaimed from expired or unblocked
/// entries — every tracked entry is a live lockout, a genuinely saturated window — a key that is not
/// tracked is refused: its wrong code is not counted and the charge reports
/// <see cref="GuessWindowCharge.Refused"/>, which the caller answers with 429 <c>rate_limited</c>. That is
/// the fail-closed answer — an untracked key never gets unlimited guesses — and it keeps every existing
/// lockout. A <em>correct</em> code does not go through the charge at all, so saturation never blocks a
/// valid TOTP; <see cref="BlockedFor"/> also keeps answering null for an untracked key. A caller never
/// sees an exception.
/// </para>
/// </remarks>
/// <param name="logger">The one throttled saturation notice (no user id, 09 README BD16).</param>
public sealed partial class InMemoryMfaGuessWindow(ILogger<InMemoryMfaGuessWindow> logger) : IMfaGuessWindow
{
    /// <summary>The number of wrong codes inside the window that blocks the user (WT35: 10).</summary>
    public const int Threshold = 10;

    /// <summary>How long a wrong code counts (WT35: 15 minutes).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The wait a blocked client is told (WT35 fixes <c>Retry-After</c> at 900 seconds, the window's
    /// length, not the remainder of it). The saturated window reuses that value: the window length is the
    /// simple, conservative answer, not the shortest remaining lockout.
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>The same value as the integer the problem's <c>Retry-After</c> carries.</summary>
    public const int RetryAfterSeconds = 900;

    /// <summary>The user ids the process tracks before it evicts entries.</summary>
    public const int MaxTrackedUsers = 10_000;

    /// <summary>
    /// The most entries one eviction sweep frees, so the O(n) scan runs once per this many inserts rather
    /// than on every insert while the dictionary is at its cap.
    /// </summary>
    public const int EvictionBatch = 1_000;

    /// <summary>How often the saturated window emits its one <c>mfa_guess_window_saturated</c> notice.</summary>
    public static readonly TimeSpan SaturationNoticeInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _wrongCodes = [];

    /// <summary>The users whose trip has already been reported, so WT35's row is written once.</summary>
    private readonly HashSet<Guid> _reported = [];

    private readonly Lock _gate = new();

    /// <summary>When the saturation notice was last emitted, so an attack cannot flood the log.</summary>
    private DateTimeOffset _saturationNoticedAt = DateTimeOffset.MinValue;

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
    public GuessWindowCharge RegisterWrongCode(Guid userId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_wrongCodes.TryGetValue(userId, out var times))
            {
                if (_wrongCodes.Count >= MaxTrackedUsers && !Evict(now))
                {
                    // The cap is held entirely by active lockouts: refuse this key's wrong code rather than
                    // evict one of them (fail closed: no unlimited guesses, every lockout kept).
                    NoticeSaturation(now);

                    return GuessWindowCharge.Refused;
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

            // Exactly the crossing call answers Tripped, so the single audit row of WT35 is written once.
            return times.Count == Threshold ? GuessWindowCharge.Tripped : GuessWindowCharge.Counted;
        }
    }

    /// <summary>
    /// Emits the one <c>mfa_guess_window_saturated</c> notice, throttled to
    /// <see cref="SaturationNoticeInterval"/>.
    /// </summary>
    /// <remarks>
    /// The message is type-only: it names the condition, never the user whose attempt was refused and never
    /// a code, so an operator sees the saturation without the log becoming a per-request firehose
    /// (09 README BD16).
    /// </remarks>
    private void NoticeSaturation(DateTimeOffset now)
    {
        if (now - _saturationNoticedAt < SaturationNoticeInterval)
        {
            return;
        }

        _saturationNoticedAt = now;
        LogSaturated(logger);
    }

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "mfa_guess_window_saturated: the wrong-code window is at capacity with active lockouts; new keys are refused (429).")]
    private static partial void LogSaturated(ILogger logger);

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
    /// Restores the user cap, evicting only entries that are not active lockouts.
    /// </summary>
    /// <remarks>
    /// Fully-expired windows go first. Still over the cap, the least-recently-wrong <em>unblocked</em>
    /// entries go next, up to <see cref="EvictionBatch"/> per sweep so the scan runs once per that many
    /// inserts. An entry whose lockout is still active is <em>never</em> a victim: if nothing else can be
    /// freed the method reports failure and the caller refuses the new key instead. Returns whether room
    /// for a new key exists after the sweep.
    /// </remarks>
    private bool Evict(DateTimeOffset now)
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

        if (_wrongCodes.Count < MaxTrackedUsers)
        {
            return true;
        }

        // Only unblocked entries may be evicted, oldest activity first; a live lockout is never chosen.
        var victims = _wrongCodes
            .Where(entry => entry.Value.Count < Threshold)
            .OrderBy(entry => entry.Value.Max())
            .Take(EvictionBatch)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var userId in victims)
        {
            Forget(userId);
        }

        return _wrongCodes.Count < MaxTrackedUsers;
    }

    /// <summary>Forgets one user completely, so the next window starts unblocked and unreported.</summary>
    private void Forget(Guid userId)
    {
        _ = _wrongCodes.Remove(userId);
        _ = _reported.Remove(userId);
    }
}
