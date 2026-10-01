namespace NightTales.Infrastructure.Security;

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
    /// <see langword="true"/> when this call is the one that crossed the threshold.
    /// </returns>
    bool RegisterWrongCode(Guid userId, DateTimeOffset now);

    /// <summary>
    /// Whether the caller is the first to report this trip, so the one audit row of WT35 is written once
    /// per window even though every later request keeps answering 429.
    /// </summary>
    /// <param name="userId">The user whose trip is reported.</param>
    /// <returns><see langword="true"/> exactly once per tripped window.</returns>
    bool TryClaimTripReport(Guid userId);
}
