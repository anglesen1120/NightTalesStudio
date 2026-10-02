using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Auth.Login.Commands;

/// <summary>What one login attempt answered (09.3.2-T1 Design; BD3/BD4).</summary>
public enum LoginOutcome
{
    /// <summary>200: an access token plus the refresh family the Api sets as a cookie.</summary>
    Tokens,

    /// <summary>401 <c>mfa_required</c>: the role always signs in with TOTP and 09.3.2-T3 will challenge it.</summary>
    MfaRequired,

    /// <summary>401 <c>mfa_enrolment_required</c>: an owner/admin who has not enrolled yet (BD4).</summary>
    EnrolmentRequired,

    /// <summary>
    /// 429 <c>rate_limited</c>: the per-user wrong-code window tripped, so no password is even checked
    /// ([RULED — README §B10 WT35]). The endpoint answers <c>Retry-After: 900</c>.
    /// </summary>
    RateLimited,
}

/// <summary>
/// The login result the endpoint turns into a response (09.3.2-T1 Design).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Access"/> is the session's token for <see cref="LoginOutcome.Tokens"/>, and the
/// enrolment-only token for <see cref="LoginOutcome.EnrolmentRequired"/> — <c>scope = mfa_enrol</c>,
/// 15 minutes, accepted only by <c>/me/mfa/setup|verify</c> (BD4). This task never puts it on the wire:
/// the route answers 401 <c>mfa_enrolment_required</c> and 09.3.2-T3 adds the pre-auth cookie that
/// carries it ([RULED — README §B10 WT22, WT39]).
/// </para>
/// <para>
/// <see cref="Refresh"/> is non-null only for <see cref="LoginOutcome.Tokens"/>: the two MFA outcomes
/// answer with no refresh cookie at all.
/// </para>
/// </remarks>
/// <param name="Outcome">Which of the three answers the attempt produced.</param>
/// <param name="Access">The access token, or the enrolment-only token; null for <c>mfa_required</c>.</param>
/// <param name="Refresh">The new family's plaintext token; null unless the login completed.</param>
/// <param name="Role">
/// The authenticated user's platform role. The frozen login body of 09.3-auth plan §5 returns
/// <c>role</c> next to the token (10-web-ui's <c>AuthRole</c> routes the shell with it), so the flow
/// carries it rather than making the endpoint decode the token it just signed.
/// </param>
/// <param name="Amr">The authentication method the token records, for the same reason (<c>amr</c>).</param>
public sealed record LoginResult(
    LoginOutcome Outcome,
    IssuedAccessToken? Access,
    IssuedRefreshSession? Refresh,
    PlatformRole Role,
    AuthMethod Amr);

/// <summary>
/// The frozen <c>POST /api/v1/auth/login</c> body (09.3-auth plan §5): the two credential fields.
/// </summary>
/// <remarks>
/// <para>
/// The caller metadata is <em>not</em> part of the body: <see cref="RequestInfo"/> is ambient
/// (<c>IRequestInfoAccessor</c>), and letting a client submit its own IP or User-Agent would fake the
/// audit trail. The handler resolves it from the port, exactly like 09.3.1-T1's registration flow.
/// </para>
/// <para>
/// <see cref="AllowAnonymousRequestAttribute"/> is the D4 marker: login is an anonymous route, and
/// without it the default-deny authorization behaviour would answer 401 before the handler runs.
/// </para>
/// </remarks>
/// <param name="Email">The submitted address; never logged.</param>
/// <param name="Password">The submitted password; never logged.</param>
[NotAudited("LoginHandler writes the account.login_succeeded/account.login_failed rows itself through IAuditWriter, with the caller's IP and User-Agent (09.3.2-T1).")]
[AllowAnonymousRequest]
public sealed record LoginCommand(
    string Email,
    string Password) : ICommand<ErrorOr<LoginResult>>;
