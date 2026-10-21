using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Refresh.Commands;

/// <summary>What one refresh attempt answered (09.3.2-T1 Design; [RULED — README §B10 WT29, WT36]).</summary>
public enum RefreshOutcome
{
    /// <summary>200: a fresh access token and the successor refresh token for the cookie.</summary>
    Tokens,

    /// <summary>401 <c>refresh_token_reused</c>: the presented row was already rotated, so the family was revoked.</summary>
    ReuseDetected,

    /// <summary>401 <c>session_revoked</c>: unknown cookie, a stale revoked row, or an expired/idle family.</summary>
    SessionRevoked,
}

/// <summary>
/// The refresh result the endpoint turns into a response (09.3.2-T1 Design).
/// </summary>
/// <remarks>
/// <para>
/// The two 401 outcomes are values, not <c>ErrorOr</c> errors, because both of them can have revoked rows
/// on the way: an error result would roll the transaction back and leave the family live
/// ([RULED — README §B10 WT29]). The endpoint maps each outcome to its documented status.
/// </para>
/// <para>
/// <c>email_not_verified</c> is deliberately <em>not</em> an outcome: that answer must revoke nothing, so
/// the flow returns it as an error and the transaction rolls back the untouched rotation.
/// </para>
/// </remarks>
/// <param name="Outcome">Which of the three answers the attempt produced.</param>
/// <param name="Access">The fresh access token; non-null only for <see cref="RefreshOutcome.Tokens"/>.</param>
/// <param name="Refresh">The successor cookie value; non-null only for <see cref="RefreshOutcome.Tokens"/>.</param>
public sealed record RefreshResult(RefreshOutcome Outcome, IssuedAccessToken? Access, RotatedSession? Refresh);

/// <summary>
/// The <c>POST /api/v1/auth/refresh</c> request (09.3.2-T1 Design): the refresh token the Api read from
/// the <c>__Secure-nts_rt</c> cookie.
/// </summary>
/// <remarks>
/// The token never travels in the body or in a query string (BD16); the endpoint fills this member from
/// the cookie. The route is anonymous in the authentication sense — a client whose access token expired
/// is exactly the client that must be able to refresh — and the cookie plus the <c>Origin</c> filter are
/// what authorise it.
/// </remarks>
/// <param name="PresentedToken">The presented cookie value; never logged.</param>
[NotAudited("RefreshHandler writes the account.refresh_reuse_detected row itself when a family is reused; a normal rotation writes none (09.3.2-T1).")]
[AllowAnonymousRequest]
public sealed record RefreshCommand(string PresentedToken) : ICommand<ErrorOr<RefreshResult>>;
