using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The re-authentication flow of 09.10.4-T1 (09 README BD4): password first, then the authenticator code
/// when the account has one, then the family's <c>reauthenticated_at</c>.
/// </summary>
/// <remarks>
/// <para>
/// The failure path is the interesting one. A wrong password or code charges the family's durable counter
/// (<c>refresh_tokens.reauth_attempts</c>, WT32), writes the <c>account.reauth_failed</c> row, alerts
/// when the account is an owner or an admin and the failure is the first one, and revokes the family when
/// the counter reaches <c>Auth:Reauth:MaxAttempts</c> — answering 401 <c>session_revoked</c> from then
/// on. All of that is returned as the success-typed <see cref="ReauthOutcome.Failed"/>, so the counter,
/// the revocation and the audit row commit before the endpoint maps it to 401 ([RULED — README §B10
/// WT29]).
/// </para>
/// <para>
/// A wrong authenticator code is also charged to the per-user window of WT35, and the first path that can
/// commit writes that ruling's single <c>account_security</c> row.
/// </para>
/// <para>
/// A caller with no live family writes nothing: there is no session to charge or to revoke, so the answer
/// is 401 <c>session_revoked</c> with no row — the same answer an unknown refresh cookie gets (WT21).
/// </para>
/// <para>
/// Every path that touches the account or one of its families takes the per-user advisory lock first
/// (<see cref="SecuritySql.LockUserAsync"/>): here, before the TOTP compare-and-set and before
/// <c>MarkReauthenticatedAsync</c> takes the family lock. The MFA lifecycle takes the same lock first, so
/// the one order is per-user lock → family lock → <c>users</c> row and a concurrent re-authentication and
/// MFA change cannot deadlock.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model: the account read.</param>
/// <param name="currentUser">The caller's <c>sub</c> and <c>sid</c> claims.</param>
/// <param name="credentials">The password verifier of 09.3.1-T1, whose re-auth path returns a bool.</param>
/// <param name="verifier">The TOTP verifier of the enrolled account (WT31).</param>
/// <param name="refreshTokens">The family store: the counter, the revocation and the stamp.</param>
/// <param name="guessWindow">The per-user wrong-code window (WT35).</param>
/// <param name="alerter">The P security alert (WT25).</param>
/// <param name="audit">The writer of this flow's <c>account_security</c> rows.</param>
/// <param name="options">The attempt ceiling (BD4: 5).</param>
/// <param name="clock">The scope's clock reading.</param>
public sealed class EfReauthService(
    AppDbContext database,
    ICurrentUser currentUser,
    ICredentialVerifier credentials,
    ITotpVerifier verifier,
    IRefreshTokenService refreshTokens,
    IMfaGuessWindow guessWindow,
    ISecurityAlerter alerter,
    IAuditWriter audit,
    IOptions<MfaOptions> options,
    IClock clock) : IReauthService
{
    /// <inheritdoc />
    public async Task<ErrorOr<ReauthResult>> ReauthenticateAsync(
        string password,
        string? code,
        RequestInfo info,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (currentUser.Id is not { } userId || currentUser.SessionId is not { } familyId)
        {
            return Failed(RevokedSession());
        }

        // One global lock order: the per-user advisory lock first, then any family lock, then the users
        // row. The re-authentication's TOTP compare-and-set writes the users row and MarkReauthenticatedAsync
        // takes the family lock, while the MFA lifecycle takes the family locks and writes the users row at
        // commit; taking this lock first makes both orders the same and removes the deadlock cycle.
        await SecuritySql.LockUserAsync(database, userId, ct).ConfigureAwait(false);

        var user = await database.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, ct)
            .ConfigureAwait(false);

        if (user is null || user.Status is not UserStatus.Active)
        {
            return Failed(RevokedSession());
        }

        var state = await refreshTokens.GetReauthStateAsync(familyId, ct).ConfigureAwait(false);
        if (state.IsError)
        {
            // A database that cannot answer rolls the attempt back (503); nothing is charged.
            return state.Errors;
        }

        if (state.Value.Revoked)
        {
            return Failed(RevokedSession());
        }

        if (!await credentials.VerifyPasswordAsync(user, password, ct).ConfigureAwait(false))
        {
            return await ChargeAsync(
                    user,
                    familyId,
                    Error.Unauthorized(ErrorCodes.InvalidCredentials, "The password is not valid."),
                    wrongCode: false,
                    info,
                    now,
                    ct)
                .ConfigureAwait(false);
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return await ChargeAsync(
                        user,
                        familyId,
                        MfaErrors.InvalidMfaCode("The authenticator code is required."),
                        wrongCode: true,
                        info,
                        now,
                        ct)
                    .ConfigureAwait(false);
            }

            var verified = await verifier.VerifyAsync(user.Id, code, TotpSlot.Active, ct).ConfigureAwait(false);

            if (verified.IsError)
            {
                return await ChargeAsync(
                        user,
                        familyId,
                        verified.FirstError,
                        wrongCode: true,
                        info,
                        now,
                        ct)
                    .ConfigureAwait(false);
            }
        }

        var stamped = await refreshTokens.MarkReauthenticatedAsync(familyId, now, ct).ConfigureAwait(false);
        if (stamped.IsError)
        {
            return stamped.Errors;
        }

        return new ReauthResult(ReauthOutcome.Succeeded, now);
    }

    /// <summary>
    /// Charges one wrong attempt to the family, audits it, alerts on an owner's or admin's first failure
    /// and revokes the family once the ceiling is reached (BD4, WT29, WT32).
    /// </summary>
    private async Task<ErrorOr<ReauthResult>> ChargeAsync(
        AppUser user,
        Guid familyId,
        Error failure,
        bool wrongCode,
        RequestInfo info,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (wrongCode)
        {
            var charge = guessWindow.RegisterWrongCode(user.Id, now);
            if (charge is GuessWindowCharge.Refused)
            {
                // The window is saturated with active lockouts, so this untracked key's wrong code is
                // refused with the same 429 a real lockout gives. Nothing is charged and nothing is
                // claimed: the key never reached the threshold.
                return Failed(MfaErrors.RateLimited(InMemoryMfaGuessWindow.RetryAfterSeconds));
            }
        }

        var charged = await refreshTokens
            .RegisterReauthFailureAsync(familyId, now, ct)
            .ConfigureAwait(false);

        if (charged.IsError)
        {
            return charged.Errors;
        }

        var attempts = charged.Value;

        await audit
            .WriteAsync(
                SecurityAudit.Entry(
                    SecurityAuditActions.ReauthFailed,
                    user.Id,
                    info,
                    Visibility.User,
                    failure.Code,
                    now),
                ct)
            .ConfigureAwait(false);

        if (wrongCode
            && guessWindow.BlockedFor(user.Id, now) is not null
            && guessWindow.TryClaimTripReport(user.Id))
        {
            await audit
                .WriteAsync(
                    SecurityAudit.Entry(
                        SecurityAuditActions.MfaCodeGuessingLocked,
                        user.Id,
                        info,
                        Visibility.Internal,
                        "wrong-code window tripped",
                        now),
                    ct)
                .ConfigureAwait(false);
        }

        if (attempts == 1 && user.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(
                        SecurityEventKinds.ReauthFailed,
                        user.Id,
                        "The first re-authentication attempt of an owner or admin failed."),
                    ct)
                .ConfigureAwait(false);
        }

        if (attempts >= options.Value.Reauth.MaxAttempts)
        {
            // BD4: the fifth wrong attempt ends the family. The Design's parenthetical reason
            // "reauth_failed" is not storable — refresh_tokens.revoked_reason's CHECK lists the
            // RevocationReason members and this task may not migrate — so the family is revoked as
            // mfa_change, the reason the enum attributes to 09.10.4-T1.
            var revoked = await refreshTokens
                .RevokeFamilyAsync(familyId, RevocationReason.MfaChange.ToWire(), ct)
                .ConfigureAwait(false);

            if (revoked.IsError)
            {
                return revoked.Errors;
            }

            return Failed(RevokedSession());
        }

        return Failed(failure);
    }

    private static ReauthResult Failed(Error failure) => new(ReauthOutcome.Failed, null, failure);

    private static Error RevokedSession() =>
        Error.Unauthorized(ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.");
}
