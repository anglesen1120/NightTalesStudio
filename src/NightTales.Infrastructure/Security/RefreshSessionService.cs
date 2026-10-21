using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Auth.Login.Commands;
using NightTales.Application.Features.Auth.Logout.Commands;
using NightTales.Application.Features.Auth.Refresh.Commands;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The credential flows of 09.3.2-T1 (login, refresh, logout) — the work behind
/// <see cref="IAuthSessionService"/>, in Infrastructure because it reads <c>AppUser</c> and the Identity
/// store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Login</b> keeps the order the task's Design fixes: the validator already ran in the pipeline, then
/// the credential verifier (unknown address and wrong password take the same timing-equalising path,
/// 09.3.1-T1), then account status, then the confirmed address, then the owner/admin MFA answers. The
/// enrolled account's answer carries the challenge document 09.3.2-T3 opens
/// (<see cref="IMfaChallengeService.BeginAsync"/>, [RULED — README §B10 WT22, WT39]); the unenrolled owner
/// or admin gets the enrolment-only token of BD4. Neither sets a refresh cookie.
/// </para>
/// <para>
/// <b>Refresh</b> rotates first and re-checks the account second, exactly as the Design says. The order is
/// load-bearing: the unconfirmed-address answer must revoke nothing, so it returns an <em>error</em>, which
/// rolls the rotation back and leaves the family usable once the address is verified; the suspended answer
/// revokes the family and therefore returns a <em>value</em>, so the revocation commits
/// ([RULED — README §B10 WT29]).
/// </para>
/// <para>
/// <b>Logout</b> revokes the presented token's whole family (WF12). A missing or unknown cookie answers
/// 401 <c>session_revoked</c> (WT21); a cookie whose family is already revoked — logout twice, or after a
/// reuse detection — answers 204, because there is nothing left to revoke and the response must not reveal
/// that the session existed.
/// </para>
/// <para>
/// Audit rows (<c>account.login_succeeded</c>/<c>account.login_failed</c>,
/// <c>refresh.reuse_detected</c>) and the reuse-detection e-mail are <c>IAuditWriter</c>'s and
/// <c>IEmailSender</c>'s, owned by 09.3.6-T1 and 09.2.6-T3. This flow raises only the wrong-code window's
/// row; the completed-login row is 09.3.2-T3's (the MFA step, where the session is actually issued), and
/// the rotation's rows are 09.3.6-T1's — the seams (one call site each) are where they attach.
/// </para>
/// </remarks>
/// <param name="database">The scope's write model: the account read and the <c>last_login_at</c> write.</param>
/// <param name="credentials">The Identity credential verifier (09.3.1-T1).</param>
/// <param name="refreshTokens">The refresh-token store.</param>
/// <param name="lookup">The indexed token → owner read of the same table.</param>
/// <param name="tokens">The ES256 issuer.</param>
/// <param name="clock">The scope's clock.</param>
/// <param name="metrics">The two counters this flow is the only writer of.</param>
/// <param name="guessWindow">The per-user wrong-code window that gates the whole sign-in (WT35).</param>
/// <param name="audit">The writer of this flow's <c>account_security</c> rows.</param>
/// <param name="challenges">
/// The MFA challenge store of 09.3.2-T3: the login step opens one challenge for every account that always
/// signs in with an authenticator, and the answer carries its document ([RULED — README §B10 WT22, WT39]).
/// </param>
public sealed class RefreshSessionService(
    AppDbContext database,
    ICredentialVerifier credentials,
    IRefreshTokenService refreshTokens,
    IRefreshTokenLookup lookup,
    ITokenIssuer tokens,
    IClock clock,
    AuthMetrics metrics,
    IMfaGuessWindow guessWindow,
    IAuditWriter audit,
    IMfaChallengeService challenges) : IAuthSessionService
{
    /// <inheritdoc />
    public async Task<ErrorOr<LoginResult>> LoginAsync(
        LoginCommand command,
        RequestInfo info,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        var verified = await credentials.VerifyAsync(command.Email, command.Password, ct).ConfigureAwait(false);
        if (verified.IsError)
        {
            metrics.Login(verified.FirstError.Code);
            return verified.Errors;
        }

        var user = verified.Value;

        if (user.Status is not UserStatus.Active)
        {
            metrics.Login(ErrorCodes.SessionRevoked);
            return Error.Unauthorized(ErrorCodes.SessionRevoked, "The account is not active.");
        }

        // [RULED — README §B10 WT35]: ten wrong codes inside fifteen minutes block this account's sign-in
        // before anything is issued. The one account_security row of that ruling is written by the first
        // path that both observes the block and can commit — this one returns a value, so its transaction
        // commits, and TryClaimTripReport keeps it to one row per window.
        if (guessWindow.BlockedFor(user.Id, now) is not null)
        {
            if (guessWindow.TryClaimTripReport(user.Id))
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

            metrics.Login(ErrorCodes.RateLimited);

            return new LoginResult(LoginOutcome.RateLimited, null, null, user.Role, AuthMethod.Pwd, null);
        }

        if (!user.EmailConfirmed)
        {
            metrics.Login(ErrorCodes.EmailNotVerified);
            return Error.Forbidden(
                ErrorCodes.EmailNotVerified,
                "The e-mail address has not been verified yet.");
        }

        if (user.Role is PlatformRole.Admin or PlatformRole.Owner || user.TwoFactorEnabled)
        {
            // BD4: an owner or admin always signs in with TOTP, and so does any account whose authenticator
            // is enrolled — 09.10.4-T1 is what can turn that flag on, so the branch must cover it or an
            // enrolled user could sign in with the password alone. 09.3.2-T3 opens the challenge for the
            // enrolled account and the endpoint puts its document in the 401 body and its pre-auth cookie
            // on the response; the unenrolled owner or admin still gets the enrolment-only token
            // (scope = mfa_enrol) with nothing on the wire. Neither answer carries a refresh cookie.
            if (!user.TwoFactorEnabled)
            {
                metrics.Login(ErrorCodes.MfaEnrolmentRequired);

                return new LoginResult(
                    LoginOutcome.EnrolmentRequired,
                    tokens.IssueEnrolmentToken(user, now),
                    null,
                    user.Role,
                    AuthMethod.Pwd,
                    null);
            }

            var challenge = await challenges.BeginAsync(user.Id, info, ct).ConfigureAwait(false);
            if (challenge.IsError)
            {
                // Only a database that cannot answer leaves Begin errorless-but-failed; its error is the
                // 503 the caller retries, and nothing was written.
                return challenge.Errors;
            }

            metrics.Login(ErrorCodes.MfaRequired);

            return new LoginResult(
                LoginOutcome.MfaRequired,
                null,
                null,
                user.Role,
                AuthMethod.Pwd,
                challenge.Value);
        }

        var session = await refreshTokens.StartSessionAsync(user, info, ct).ConfigureAwait(false);
        if (session.IsError)
        {
            return session.Errors;
        }

        // The last successful login is part of the account, so it is written through the tracked entity and
        // commits with the family the same UnitOfWork transaction holds.
        user.LastLoginAt = now;
        metrics.Login("ok");

        return new LoginResult(
            LoginOutcome.Tokens,
            tokens.IssueAccessToken(user, session.Value.FamilyId, AuthMethod.Pwd, now),
            session.Value,
            user.Role,
            AuthMethod.Pwd,
            null);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<RefreshResult>> RefreshAsync(
        RefreshCommand command,
        RequestInfo info,
        CancellationToken ct)
    {
        var rotated = await refreshTokens.RotateAsync(command.PresentedToken, info, ct).ConfigureAwait(false);
        if (rotated.IsError)
        {
            // WT29: the two answers that may have revoked rows are values, not errors, so whatever the
            // rotation wrote commits and a fresh context sees it.
            metrics.Refresh(rotated.FirstError.Code);

            return rotated.FirstError.Code switch
            {
                ErrorCodes.RefreshTokenReused => new RefreshResult(RefreshOutcome.ReuseDetected, null, null),
                ErrorCodes.SessionRevoked => new RefreshResult(RefreshOutcome.SessionRevoked, null, null),
                _ => rotated.Errors,
            };
        }

        var familyId = rotated.Value.FamilyId;

        // "One indexed read" of the family's owner (09.3.2-T1 Design). In P this is the only place a
        // suspension is enforced, so a suspended account cannot keep renewing for up to 14 days (WF12).
        var owner = await lookup.FindAsync(command.PresentedToken, ct).ConfigureAwait(false);
        if (owner.IsError)
        {
            // A dead cookie is the 401 outcome (nothing left to commit); anything else — a database that
            // cannot answer — is an error, which rolls the rotation back so the client can retry.
            if (owner.FirstError.Code != ErrorCodes.SessionRevoked)
            {
                return owner.Errors;
            }

            metrics.Refresh(ErrorCodes.SessionRevoked);
            return new RefreshResult(RefreshOutcome.SessionRevoked, null, null);
        }

        // `FindAsync` answers from the change tracker, where the rotation has already loaded this user for
        // its role; the lookup above is the only statement this re-check needs (CR-4).
        var user = await database.Users.FindAsync([owner.Value.UserId], ct).ConfigureAwait(false);

        if (user is null || user.Status is not UserStatus.Active)
        {
            await refreshTokens
                .RevokeFamilyAsync(familyId, RevocationReason.AccountNotActive.ToWire(), ct)
                .ConfigureAwait(false);

            metrics.Refresh(ErrorCodes.SessionRevoked);
            return new RefreshResult(RefreshOutcome.SessionRevoked, null, null);
        }

        if (!user.EmailConfirmed)
        {
            metrics.Refresh(ErrorCodes.EmailNotVerified);

            // An error rolls the rotation back: the presented cookie stays live, and the user keeps the
            // session by verifying the address (09.3.2-T1 Design).
            return Error.Forbidden(
                ErrorCodes.EmailNotVerified,
                "The e-mail address has not been verified yet.");
        }

        metrics.Refresh("ok");

        return new RefreshResult(
            RefreshOutcome.Tokens,
            tokens.IssueAccessToken(user, familyId, AuthMethod.Pwd, clock.UtcNow),
            rotated.Value);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> LogoutAsync(LogoutCommand command, CancellationToken ct)
    {
        var owner = await lookup.FindAsync(command.PresentedToken, ct).ConfigureAwait(false);

        if (owner.IsError)
        {
            // A missing or unknown cookie has no family to revoke (WT21); the endpoint clears the cookie
            // either way, so the client cannot stay signed in with a token the API does not know. A database
            // that cannot answer keeps its own code, so the caller sees 503 rather than a wrong 401.
            return owner.Errors;
        }

        return await refreshTokens
            .RevokeFamilyAsync(owner.Value.FamilyId, RevocationReason.Logout.ToWire(), ct)
            .ConfigureAwait(false);
    }
}
