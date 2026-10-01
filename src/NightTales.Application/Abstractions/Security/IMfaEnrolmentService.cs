using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The enrolment material <c>POST /me/mfa/setup</c> answers with (09.10.4-T1 Design).
/// </summary>
/// <remarks>
/// The secret is returned exactly once, to be rendered as a QR code by the SPA — this task adds no QR
/// library. <see cref="OtpauthUri"/> is the same value in the <c>otpauth://</c> form an authenticator
/// application understands, with <c>issuer=NightTales</c> (BD4).
/// </remarks>
/// <param name="Secret">The base32 secret; never logged.</param>
/// <param name="OtpauthUri">The <c>otpauth://totp/…</c> URI the SPA renders.</param>
public sealed record MfaSetup(string Secret, string OtpauthUri);

/// <summary>
/// The four MFA-lifecycle commands behind one seam: setup, confirmation, recovery-code regeneration and
/// disable (09.10.4-T1 Design).
/// </summary>
/// <remarks>
/// <para>
/// The flow lives behind a port for the same layer reason as 09.3.2-T1's
/// <see cref="IAuthSessionService"/>: it reads <c>AppUser</c> (the account's e-mail for the
/// <c>otpauth</c> label, its role for the alert rule, and <c>two_factor_enabled</c>), and
/// <c>AppUser</c> is an Infrastructure type, so the Application assembly cannot reference it (09 README
/// D1/D2, enforced by <c>AssemblyReferenceRules</c>). The command handlers stay thin delegators, and the
/// five lower-level ports this task owns (<see cref="ITotpVerifier"/>, <see cref="ITotpSecretStore"/>,
/// <see cref="IRecoveryCodeService"/>, <see cref="IReauthGuard"/>, <see cref="ISecurityAlerter"/>) are
/// what the flow itself is built from.
/// </para>
/// <para>
/// Every method runs inside the caller's transaction (09 README D5): the secret write, the
/// <c>two_factor_enabled</c> flag, the stamp rotation and the audit rows commit or roll back together.
/// Each one also writes its <c>account_security</c> row through <c>IAuditWriter</c> and alerts through
/// <see cref="ISecurityAlerter"/> where the Design says so.
/// </para>
/// </remarks>
public interface IMfaEnrolmentService
{
    /// <summary>
    /// Starts (or restarts) the enrolment: a fresh 20-byte secret becomes the pending one.
    /// </summary>
    /// <param name="userId">The caller, resolved by the handler from <c>ICurrentUser</c>.</param>
    /// <param name="info">The caller metadata the audit row records.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The secret and its <c>otpauth://</c> URI, or 404 <c>not_found</c> when the account is gone.
    /// </returns>
    Task<ErrorOr<MfaSetup>> StartSetupAsync(Guid userId, RequestInfo info, CancellationToken ct);

    /// <summary>
    /// Confirms the enrolment with a code from the pending secret: the secret is promoted,
    /// <c>two_factor_enabled</c> becomes true, the stamp rotates and ten recovery codes are issued.
    /// </summary>
    /// <param name="userId">The caller, resolved by the handler from <c>ICurrentUser</c>.</param>
    /// <param name="code">The submitted TOTP code; never logged.</param>
    /// <param name="info">The caller metadata the audit row records.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>The ten recovery codes, in plaintext, once; 401 <c>invalid_mfa_code</c> for a wrong code.</returns>
    Task<ErrorOr<IReadOnlyList<string>>> VerifyAsync(
        Guid userId,
        string code,
        RequestInfo info,
        CancellationToken ct);

    /// <summary>Replaces all recovery codes and returns the ten new ones once.</summary>
    /// <param name="userId">The caller, resolved by the handler from <c>ICurrentUser</c>.</param>
    /// <param name="info">The caller metadata the audit row records.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid userId,
        RequestInfo info,
        CancellationToken ct);

    /// <summary>
    /// Disables MFA: both secrets and the recovery codes are deleted, <c>two_factor_enabled</c> becomes
    /// false, the stamp rotates and every family but the caller's own is revoked (BD4).
    /// </summary>
    /// <param name="userId">The caller, resolved by the handler from <c>ICurrentUser</c>.</param>
    /// <param name="info">The caller metadata the audit row records.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> DisableAsync(Guid userId, RequestInfo info, CancellationToken ct);
}
