using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// One security event worth an operator's attention (09.10.4-T1 Design, 09 README BD4/WT25).
/// </summary>
/// <remarks>
/// The record carries no secret, no e-mail address and no IP (09 README BD16): <see cref="Kind"/> is a
/// value of <see cref="SecurityEventKinds"/>, <see cref="SubjectUserId"/> is the account the event is
/// about, and <see cref="Detail"/> is a short human label from this codebase's own closed vocabulary.
/// </remarks>
/// <param name="Kind">The event kind, one of <see cref="SecurityEventKinds"/>.</param>
/// <param name="SubjectUserId">The account the event is about, or null for a pure system event.</param>
/// <param name="Detail">A short, non-identifying description, or null.</param>
public sealed record SecurityEvent(string Kind, Guid? SubjectUserId, string? Detail);

/// <summary>
/// The closed vocabulary of <see cref="SecurityEvent.Kind"/> (09.10.4-T1 Design).
/// </summary>
/// <remarks>
/// The S-phase owner e-mail fan-out and the 11.6.2 alert pipeline key on these values — the plan's
/// template names <c>security_mfa_enrolled</c>, <c>security_mfa_disabled</c> and
/// <c>security_reauth_failed</c> are the e-mail side of the first three kinds [INFERENCE names].
/// </remarks>
public static class SecurityEventKinds
{
    /// <summary>An owner or admin enrolled an authenticator.</summary>
    public const string MfaEnrolled = "security.mfa_enrolled";

    /// <summary>A user disabled MFA.</summary>
    public const string MfaDisabled = "security.mfa_disabled";

    /// <summary>A user replaced their recovery codes.</summary>
    public const string MfaRecoveryCodesRegenerated = "security.mfa_recovery_codes_regenerated";

    /// <summary>A re-authentication failed; the first failure of an owner or admin is alerted (BD4).</summary>
    public const string ReauthFailed = "security.reauth_failed";

    /// <summary>
    /// The first wrong authenticator or recovery code of an owner's or admin's MFA login step
    /// (09.3.2-T3 Design). [INFERENCE name]
    /// </summary>
    /// <remarks>
    /// The S-phase alert rules were frozen without a kind for a failed second step (README §B10 line
    /// 1108 lists <c>security.mfa_changed</c> and <c>security.recovery_code_used</c>), and
    /// <c>security.reauth_failed</c> is the re-authentication's own event — so this task adds the name
    /// the same way 09.10.4-T1 added <see cref="ReauthFailed"/>, and the S-phase catalogue adopts it.
    /// </remarks>
    public const string MfaLoginFailed = "security.mfa_login_failed";

    /// <summary>
    /// A recovery code completed an owner's or admin's sign-in (BD4, README §B10 line 1108: frozen as
    /// <c>security.recovery_code_used</c>).
    /// </summary>
    public const string RecoveryCodeUsed = "security.recovery_code_used";

    /// <summary>
    /// An owner's or admin's sign-in completed from a different address than the password step
    /// (09 §4 story 09.3.2, README §B10 line 1108: frozen as <c>security.owner_login_new_ip</c>).
    /// </summary>
    public const string OwnerLoginNewIp = "security.owner_login_new_ip";

    /// <summary>
    /// A stored authenticator secret could not be read back (WF15): a P1 condition that must never be
    /// mistaken for "MFA is off" (09.10.4-T1 failure handling).
    /// </summary>
    public const string TotpSecretUnreadable = "security.totp_secret_unreadable";
}

/// <summary>
/// Raises a security event for the operator (09 README BD4, [RULED — README §B10 WT25]).
/// </summary>
/// <remarks>
/// <para>
/// In P the implementation writes one <c>account_security</c> audit row with
/// <c>visibility = internal</c> and one redacted structured log line. The owner e-mail fan-out and the
/// 11.6.2 alert pipeline are S: they consume the same call sites and the same
/// <see cref="SecurityEventKinds"/>, so no caller changes when they land.
/// </para>
/// <para>
/// Like <c>IAuditWriter</c>, the implementation joins the caller's transaction (09 README D5), so an
/// alert is never raised for a change that rolled back.
/// </para>
/// </remarks>
public interface ISecurityAlerter
{
    /// <summary>Records and logs one security event.</summary>
    /// <param name="evt">The event; never carries a secret, an address or an IP.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task AlertAsync(SecurityEvent evt, CancellationToken ct);
}

/// <summary>
/// The one place that rotates <c>users.security_stamp</c> (09.10.4-T1 Design, 09 W3 CR-20, D9).
/// </summary>
/// <remarks>
/// <para>
/// The port is declared here because 09.10.4-T1 is its first caller while its full implementation —
/// the per-request stamp/session check, the <c>UserSecurityChanged</c> outbox event and the
/// connection abort — belongs to <c>09.3.2-T2</c> (their task file lists it in
/// <c>Application/Abstractions/Security/</c>). This task therefore calls the port instead of assigning
/// the stamp inline, and ships the P implementation of the rotation itself.
/// </para>
/// <para>
/// <b>P limitation.</b> The P implementation rotates the stamp only; the <c>user_security_changed.v1</c>
/// outbox row, its handler and the cache eviction are 09.3.2-T2's (the current
/// <c>OutboxSaveChangesInterceptor</c> only stores events raised by an <c>AggregateRoot</c>, so that
/// task adds the raising path together with the handler). Nothing in P reads the stamp claim yet, so no
/// caller observes the difference.
/// </para>
/// </remarks>
public interface ISecurityEventWriter
{
    /// <summary>
    /// Gives the user a new <c>security_stamp</c>, so every token issued with the old one stops being
    /// accepted once 09.3.2-T2 validates the claim.
    /// </summary>
    /// <param name="userId">The user whose stamp rotates.</param>
    /// <param name="reason">The closed reason value (<c>mfa_enrolled</c>, <c>mfa_disabled</c>, …).</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>Success, or 404 <c>not_found</c> when the account does not exist.</returns>
    Task<ErrorOr<Success>> RotateStampAsync(Guid userId, string reason, CancellationToken ct);
}
