using NightTales.Application.Publishing.Quota;

namespace NightTales.Application.Features.Publishing.Upload;

/// <summary>
/// The retry instant of a quota-limited upload (06.2.3-T2 plan §7, 06 README D10): the next Pacific midnight plus a
/// bounded 0–10 minute jitter.
/// </summary>
/// <remarks>
/// <para>
/// <b>No second boundary rule.</b> The midnight itself is <see cref="PacificQuotaDay.NextResetAt"/>, the one function
/// 06.3.1-T1 derived from the zone's rules (a fixed <c>-07:00</c> would reset an hour late or early across the two
/// transitions, and a spring-forward midnight does not exist locally). This type only adds the jitter and nothing
/// else, so the quota day the ledger counts on and the instant the row wakes up can never disagree
/// (STATUS S69-7).
/// </para>
/// <para>
/// <b>Why jitter.</b> Every row a spent day reschedules would otherwise wake at the same instant and race for the
/// first units of the new day; spreading them over ten minutes keeps the ledger's first minute from starving the
/// rest. The window is inclusive of both ends and the jitter is stored as given by the caller, so a row's instant
/// never re-jitters on a retry of the same attempt.
/// </para>
/// </remarks>
public static class PtDay
{
    /// <summary>The largest jitter a rescheduled upload may take, in minutes (inclusive).</summary>
    public const int MaxJitterMinutes = 10;

    /// <summary>
    /// The first instant of the next Pacific quota day for <paramref name="now"/>, which is the delay
    /// <see cref="QuotaExhaustedDelay"/>'s caller adds the jitter to.
    /// </summary>
    /// <param name="now">The job's clock reading.</param>
    /// <returns>The next Pacific local midnight as a UTC instant, strictly after <paramref name="now"/>.</returns>
    public static DateTimeOffset NextPtMidnight(DateTimeOffset now) => PacificQuotaDay.NextResetAt(now);

    /// <summary>One jitter of this platform's bound: <c>0..</c><see cref="MaxJitterMinutes"/> minutes, inclusive.</summary>
    /// <returns>A fresh pseudo-random minute count.</returns>
    public static int NextJitterMinutes() => Random.Shared.Next(0, MaxJitterMinutes + 1);

    /// <summary>The next quota-reset instant plus a fresh bounded jitter — the value a quota reschedule stores.</summary>
    /// <param name="now">The job's clock reading.</param>
    /// <returns>The instant the row is due again.</returns>
    public static DateTimeOffset NextRetryAt(DateTimeOffset now) => NextRetryAt(now, NextJitterMinutes());

    /// <summary>The next quota-reset instant plus the jitter the caller already drew.</summary>
    /// <param name="now">The job's clock reading.</param>
    /// <param name="jitterMinutes">A minute count in <c>0..</c><see cref="MaxJitterMinutes"/>.</param>
    /// <returns>The instant the row is due again.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="jitterMinutes"/> is negative or larger than <see cref="MaxJitterMinutes"/>.
    /// </exception>
    public static DateTimeOffset NextRetryAt(DateTimeOffset now, int jitterMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(jitterMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterMinutes, MaxJitterMinutes);

        return NextPtMidnight(now).AddMinutes(jitterMinutes);
    }

    /// <summary>Whether <paramref name="instant"/> is inside the window a reschedule of <paramref name="now"/> may use.</summary>
    /// <param name="now">The job's clock reading the reschedule was computed from.</param>
    /// <param name="instant">The stored <c>next_attempt_at</c>.</param>
    /// <returns><see langword="true"/> when the instant is the next midnight plus 0–10 minutes.</returns>
    public static bool IsInRetryWindow(DateTimeOffset now, DateTimeOffset instant)
    {
        var midnight = NextPtMidnight(now);

        return instant >= midnight && instant <= midnight.AddMinutes(MaxJitterMinutes);
    }
}
