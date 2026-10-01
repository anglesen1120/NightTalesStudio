using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// Which stored secret a submitted TOTP code is verified against (09.10.4-T1 Design,
/// [RULED — README §B10 WT31]).
/// </summary>
/// <remarks>
/// The slot is explicit because the two secrets live side by side in Identity's <c>user_tokens</c>
/// (<c>totp_secret</c> and the unverified <c>totp_secret_pending</c>), and only the enrolment route may
/// ever accept a code computed from the pending one: a pending secret that leaked before it was
/// confirmed must not be able to sign a session in.
/// </remarks>
public enum TotpSlot
{
    /// <summary>The unverified enrolment secret; only <c>VerifyMfaCommand</c> passes it (WT31).</summary>
    Pending,

    /// <summary>
    /// The active secret; password login (09.3.2-T1), <c>/auth/login/mfa</c> (09.3.2-T3) and
    /// <c>/auth/reauth</c> (09.10.4-T1) pass it.
    /// </summary>
    Active,
}

/// <summary>
/// RFC 6238 TOTP verification with the replay-proof compare-and-set (09 README BD4, WT31): the one
/// verifier every challenge, every enrolment confirmation and every re-authentication uses.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is fixed: HMAC-SHA1, the configured step (BD4: 30 s), six digits and a window of
/// ± one step. The candidate steps are compared with
/// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/> against the
/// computed code, and the accepted step is written to <c>users.last_totp_step</c> by a
/// compare-and-set, so a code can never be presented twice (BD4, W6) — that is the reason this task
/// does not use Identity's replayable <c>VerifyTwoFactorTokenAsync</c>.
/// </para>
/// <para>
/// A code is never logged, and neither is the secret it was computed from (09 README BD16).
/// </para>
/// </remarks>
public interface ITotpVerifier
{
    /// <summary>
    /// Verifies <paramref name="code"/> against the secret of <paramref name="slot"/> and records the
    /// matched step.
    /// </summary>
    /// <param name="userId">The user whose secret is verified.</param>
    /// <param name="code">The six-digit code the caller submitted; never logged.</param>
    /// <param name="slot">The secret to verify against (WT31).</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The step the accepted code belongs to, or 401 <c>invalid_mfa_code</c> — for a wrong code, for a
    /// missing or unreadable secret, and for a step that was already used (replay).
    /// </returns>
    Task<ErrorOr<long>> VerifyAsync(Guid userId, string code, TotpSlot slot, CancellationToken ct);
}
