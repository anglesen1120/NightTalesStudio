namespace NightTales.Infrastructure.Security;

/// <summary>What charging one wrong code produced (WT35).</summary>
/// <remarks>
/// <see cref="Refused"/> is the fail-closed answer of a saturated window: while the user cap is held
/// entirely by active lockouts, a key that is not tracked cannot be admitted without evicting somebody's
/// lockout, so its wrong code is refused and the caller answers 429 <c>rate_limited</c> — the same answer a
/// real lockout gives. A <em>correct</em> code never passes through this call, so saturation cannot block a
/// valid TOTP; and the refused key must not claim the trip report, because it never reached the threshold.
/// </remarks>
public enum GuessWindowCharge
{
    /// <summary>The wrong code was counted; the window is not yet at the threshold.</summary>
    Counted,

    /// <summary>This call crossed the threshold, so the one report of that trip may be claimed.</summary>
    Tripped,

    /// <summary>The window is saturated with active lockouts; the wrong code is refused (429).</summary>
    Refused,
}

/// <summary>
/// The per-user wrong-code window of [RULED — README §B10 WT35]: wrong TOTP and recovery codes are
/// counted <em>per user</em>, across every challenge and every re-authentication.
/// </summary>
/// <remarks>
/// <para>
/// Ten wrong codes inside fifteen minutes make <c>/auth/login</c> and <c>/auth/login/mfa</c> answer 429
/// <c>rate_limited</c> with <c>Retry-After: 900</c> for that user, on top of the per-challenge
/// five-attempt death 09.3.2-T3 owns.
/// </para>
/// <para>
/// <b>P storage.</b> The window is held in the Api process's memory, because this task may add no table
/// (the Design rules out a migration) and no existing row is per-user: the durable per-family counter
/// lives in <c>refresh_tokens.reauth_attempts</c>, which is a different rule. Two Api instances
/// therefore count independently, and a restart forgets the window; the cross-instance limit belongs to
/// the S-phase brute-force work (<c>trusted_devices</c>, <c>mfa_locked_untrusted</c>, WT26/09.10.4-T3),
/// exactly like the per-instance rate limiter of WT48.
/// </para>
/// <para>
/// The single <c>account_security</c> row WT35 asks for is written by the first path that both observes
/// the blocked window and can commit — the <c>/auth/login</c> gate and a failed re-authentication. A
/// path that answers an error (the enrolment verification) rolls its transaction back, so it counts the
/// wrong code and leaves the row to the next committing path:
/// <see cref="TryClaimTripReport"/> makes that "exactly once" without a table.
/// </para>
/// </remarks>
public interface IMfaGuessWindow
{
    /// <summary>
    /// Whether the user has tripped the window, and for how long a client must wait.
    /// </summary>
    /// <remarks>
    /// A key the window does not track is answered <see langword="null"/>, even while the window is
    /// saturated: this method is consulted before verification on some routes, and a correct code must
    /// never be blocked by saturation. Only <see cref="RegisterWrongCode"/> refuses a wrong code.
    /// </remarks>
    /// <param name="userId">The user to read.</param>
    /// <param name="now">The scope's clock reading.</param>
    /// <returns>
    /// <see cref="InMemoryMfaGuessWindow.RetryAfter"/> while at least
    /// <see cref="InMemoryMfaGuessWindow.Threshold"/> wrong codes are inside the window, otherwise null.
    /// </returns>
    TimeSpan? BlockedFor(Guid userId, DateTimeOffset now);

    /// <summary>
    /// Charges one wrong code to the user.
    /// </summary>
    /// <param name="userId">The user the wrong code belongs to.</param>
    /// <param name="now">The scope's clock reading.</param>
    /// <returns>
    /// <see cref="GuessWindowCharge.Tripped"/> when this call is the one that crossed the threshold,
    /// <see cref="GuessWindowCharge.Refused"/> when a saturated window refuses the key, otherwise
    /// <see cref="GuessWindowCharge.Counted"/>.
    /// </returns>
    GuessWindowCharge RegisterWrongCode(Guid userId, DateTimeOffset now);

    /// <summary>
    /// Whether the caller is the first to report this trip, so the one audit row of WT35 is written once
    /// per window even though every later request keeps answering 429.
    /// </summary>
    /// <param name="userId">The user whose trip is reported.</param>
    /// <returns><see langword="true"/> exactly once per tripped window.</returns>
    bool TryClaimTripReport(Guid userId);
}
