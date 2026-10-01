using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The MFA lifecycle of 09.10.4-T1: setup, confirmation, recovery-code regeneration and disable, in
/// Infrastructure because every one of them reads or writes <c>AppUser</c>.
/// </summary>
/// <remarks>
/// <para>
/// The flow is the one place that knows the order the Design fixes. Setup writes the pending secret and
/// audits <c>mfa.setup_started</c>. Confirmation verifies against the <em>pending</em> slot (WT31),
/// promotes the secret, turns <c>two_factor_enabled</c> on, rotates the stamp through
/// <see cref="ISecurityEventWriter"/> (never inline), issues ten recovery codes and audits
/// <c>mfa.enrolled</c> — plus one alert when the account is an owner or an admin. Disable deletes the
/// secrets and codes, turns the flag off, rotates the stamp, revokes every family but the caller's own
/// and audits <c>mfa.disabled</c>.
/// </para>
/// <para>
/// Every step runs inside the caller's transaction (09 README D5), so the secret, the flag, the stamp,
/// the revocation and the audit row commit or roll back together. The recovery codes are returned in
/// plaintext exactly once — the response that issues them — and never stored that way (BD16).
/// </para>
/// <para>
/// A wrong code on the confirmation path is counted in the per-user window of WT35
/// (<see cref="IMfaGuessWindow"/>); because this path answers an <em>error</em>, its transaction rolls
/// back, so the single <c>account_security</c> row of that ruling is written by the first committing path
/// that observes the blocked window — the login gate and a failed re-authentication. This path never claims
/// the report: a claim that outlives the rolled-back row would suppress the alert for the whole window.
/// </para>
/// <para>
/// Setup, confirmation and disable take one per-user lock, so a concurrent setup cannot replace the
/// pending secret between the code check and the promotion (which would activate an unverified secret),
/// and two concurrent confirmations cannot both succeed.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model: the account read and the <c>two_factor_enabled</c> write.</param>
/// <param name="currentUser">The caller, for the family the disable path spares.</param>
/// <param name="secrets">The two secrets of the account (WT31).</param>
/// <param name="recoveryCodes">The single-use recovery codes.</param>
/// <param name="verifier">The TOTP verifier of the confirmation step.</param>
/// <param name="securityEvents">The one writer that rotates the security stamp.</param>
/// <param name="alerter">The P security alert (WT25).</param>
/// <param name="guessWindow">The per-user wrong-code window (WT35).</param>
/// <param name="refreshTokens">The family store the disable path revokes through.</param>
/// <param name="audit">The writer of this flow's <c>account_security</c> rows.</param>
/// <param name="options">The configured issuer and step, used by the <c>otpauth://</c> URI.</param>
/// <param name="clock">The scope's clock reading.</param>
public sealed class EfMfaEnrolmentService(
    AppDbContext database,
    ICurrentUser currentUser,
    ITotpSecretStore secrets,
    IRecoveryCodeService recoveryCodes,
    ITotpVerifier verifier,
    ISecurityEventWriter securityEvents,
    ISecurityAlerter alerter,
    IMfaGuessWindow guessWindow,
    IRefreshTokenService refreshTokens,
    IAuditWriter audit,
    IOptions<MfaOptions> options,
    IClock clock) : IMfaEnrolmentService
{
    /// <inheritdoc />
    public async Task<ErrorOr<MfaSetup>> StartSetupAsync(
        Guid userId,
        RequestInfo info,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Serialize the whole lifecycle for this account (setup/verify/disable): the pending secret is
        // read, verified, promoted and deleted across several statements, and the lock is what keeps a
        // concurrent setup from replacing the payload between the read and the promotion.
        await LockUserAsync(userId, ct).ConfigureAwait(false);

        var user = await FindUserAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Errors.NotFound();
        }

        var secret = Base32.NewSecret();

        var stored = await secrets.SetPendingSecretAsync(userId, secret, ct).ConfigureAwait(false);
        if (stored.IsError)
        {
            return stored.Errors;
        }

        await audit
            .WriteAsync(
                SecurityAudit.Entry(
                    SecurityAuditActions.MfaSetupStarted,
                    userId,
                    info,
                    Visibility.User,
                    "enrolment started",
                    now),
                ct)
            .ConfigureAwait(false);

        return new MfaSetup(secret, OtpauthUri(Label(user, userId), secret));
    }

    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyList<string>>> VerifyAsync(
        Guid userId,
        string code,
        RequestInfo info,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        await LockUserAsync(userId, ct).ConfigureAwait(false);

        var user = await FindUserAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Errors.NotFound();
        }

        var verified = await verifier.VerifyAsync(userId, code, TotpSlot.Pending, ct).ConfigureAwait(false);
        if (verified.IsError)
        {
            // WT35: the wrong code is charged to the user, whatever the answer this route gives. This
            // branch answers an *error*, so UnitOfWorkBehavior rolls its transaction back: claiming the
            // trip report here would consume the claim while the audit row it guards never commits, and
            // the whole window would end with no row. The claim therefore belongs to a committing path
            // (the login gate and a failed re-authentication), which is where TryClaimTripReport is called.
            _ = guessWindow.RegisterWrongCode(userId, now);

            return verified.Errors;
        }

        var promoted = await secrets.PromotePendingSecretAsync(userId, ct).ConfigureAwait(false);
        if (promoted.IsError)
        {
            return promoted.Errors;
        }

        user.TwoFactorEnabled = true;

        var rotated = await securityEvents.RotateStampAsync(userId, "mfa_enrolled", ct).ConfigureAwait(false);
        if (rotated.IsError)
        {
            return rotated.Errors;
        }

        // BD4/WF12: an MFA change ends the account's other sessions, exactly as the disable path does. A
        // token stolen before the account was protected must not survive the enrolment and keep rotating
        // into fresh password-only access tokens. The caller's own re-authenticated family is spared; the
        // enrolment-only token has no family, so nothing is spared for it.
        var revoked = await refreshTokens
            .RevokeAllFamiliesAsync(
                userId,
                RevocationReason.MfaChange.ToWire(),
                currentUser.SessionId,
                ct)
            .ConfigureAwait(false);

        if (revoked.IsError)
        {
            return revoked.Errors;
        }

        var codes = await recoveryCodes.RegenerateAsync(userId, ct).ConfigureAwait(false);
        if (codes.IsError)
        {
            return codes.Errors;
        }

        await audit
            .WriteAsync(
                SecurityAudit.Entry(
                    SecurityAuditActions.MfaEnrolled,
                    userId,
                    info,
                    Visibility.User,
                    null,
                    now),
                ct)
            .ConfigureAwait(false);

        if (user.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(SecurityEventKinds.MfaEnrolled, userId, "An owner or admin enrolled."),
                    ct)
                .ConfigureAwait(false);
        }

        return codes.Value.ToErrorOr();
    }

    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyList<string>>> RegenerateRecoveryCodesAsync(
        Guid userId,
        RequestInfo info,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        var user = await FindUserAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Errors.NotFound();
        }

        if (!user.TwoFactorEnabled)
        {
            // [INFERENCE] The Design does not name this edge: without an enrolled authenticator there is
            // no recovery-code set to replace, and 404 is the answer the platform gives for a resource
            // the caller does not have (09 README D10).
            return Errors.NotFound();
        }

        var codes = await recoveryCodes.RegenerateAsync(userId, ct).ConfigureAwait(false);
        if (codes.IsError)
        {
            return codes.Errors;
        }

        await audit
            .WriteAsync(
                SecurityAudit.Entry(
                    SecurityAuditActions.MfaRecoveryCodesRegenerated,
                    userId,
                    info,
                    Visibility.User,
                    null,
                    now),
                ct)
            .ConfigureAwait(false);

        await alerter
            .AlertAsync(
                new SecurityEvent(
                    SecurityEventKinds.MfaRecoveryCodesRegenerated,
                    userId,
                    "The recovery codes were replaced."),
                ct)
            .ConfigureAwait(false);

        return codes.Value.ToErrorOr();
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> DisableAsync(Guid userId, RequestInfo info, CancellationToken ct)
    {
        var now = clock.UtcNow;

        await LockUserAsync(userId, ct).ConfigureAwait(false);

        var user = await FindUserAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Errors.NotFound();
        }

        if (!user.TwoFactorEnabled)
        {
            // [INFERENCE] The Design does not name this edge, and the recovery-codes route answers it the
            // same way: without an enrolled authenticator there is nothing to disable, so the answer is the
            // platform's 404 for a resource the caller does not have (D10) and nothing is written — no
            // deletion, no stamp rotation, no family revocation, no audit row, no alert.
            return Errors.NotFound();
        }

        var deleted = await secrets.DeleteAsync(userId, ct).ConfigureAwait(false);
        if (deleted.IsError)
        {
            return deleted.Errors;
        }

        user.TwoFactorEnabled = false;

        var rotated = await securityEvents.RotateStampAsync(userId, "mfa_disabled", ct).ConfigureAwait(false);
        if (rotated.IsError)
        {
            return rotated.Errors;
        }

        // BD4/WF12: an MFA change ends every other session of the account. The caller's own family is
        // spared, so the request that disabled MFA does not sign itself out.
        var revoked = await refreshTokens
            .RevokeAllFamiliesAsync(
                userId,
                RevocationReason.MfaChange.ToWire(),
                currentUser.SessionId,
                ct)
            .ConfigureAwait(false);

        if (revoked.IsError)
        {
            return revoked.Errors;
        }

        await audit
            .WriteAsync(
                SecurityAudit.Entry(
                    SecurityAuditActions.MfaDisabled,
                    userId,
                    info,
                    Visibility.User,
                    null,
                    now),
                ct)
            .ConfigureAwait(false);

        await alerter
            .AlertAsync(
                new SecurityEvent(SecurityEventKinds.MfaDisabled, userId, "MFA disabled."),
                ct)
            .ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Takes the account's lifecycle lock, so setup, confirmation and disable are serialized per user
    /// (the pending secret is read, verified, promoted and deleted across several statements).
    /// </summary>
    private Task LockUserAsync(Guid userId, CancellationToken ct) =>
        SecuritySql.LockUserAsync(database, userId, ct);

    /// <summary>The account, tracked, or null when it no longer exists.</summary>
    private Task<AppUser?> FindUserAsync(Guid userId, CancellationToken ct) =>
        database.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, ct);

    /// <summary>The <c>otpauth</c> label's account part: the address, or the user name as a fallback.</summary>
    private static string Label(AppUser user, Guid userId) =>
        user.Email ?? user.UserName ?? userId.ToString();

    /// <summary>
    /// The <c>otpauth://totp/…</c> URI of BD4: the issuer and account as the label, then the secret, the
    /// issuer, SHA1, six digits and the configured step — the SPA renders it, this task needs no QR
    /// library.
    /// </summary>
    private string OtpauthUri(string account, string secret)
    {
        var issuer = options.Value.Mfa.Issuer;

        return string.Concat(
            "otpauth://totp/",
            issuer,
            ":",
            Uri.EscapeDataString(account),
            "?secret=",
            secret,
            "&issuer=",
            Uri.EscapeDataString(issuer),
            "&algorithm=SHA1&digits=",
            Rfc6238.Digits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "&period=",
            options.Value.Mfa.StepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
