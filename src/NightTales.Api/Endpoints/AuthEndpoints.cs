using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Api.Http.Cookies;
using NightTales.Api.Http.Filters;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Auth.Login.Commands;
using NightTales.Application.Features.Auth.Login.Mfa.Commands;
using NightTales.Application.Features.Auth.Logout.Commands;
using NightTales.Application.Features.Auth.Mfa.Reauth.Commands;
using NightTales.Application.Features.Auth.Refresh.Commands;
using NightTales.Application.Features.Auth.Register.Commands;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Security;

// `SessionOptions` also names the ASP.NET Core session middleware's options; this task's type is the
// refresh-session policy of 09.3.2-T1, so the alias keeps the endpoint file unambiguous.
using SessionOptions = NightTales.Infrastructure.Security.SessionOptions;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The <c>/auth</c> route group (09 README D14/D7): register (09.3.1-T1) plus this task's login, refresh
/// and logout.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are deliberately thin (09.1.2-T1's rule): they bind the frozen bodies, send the command
/// through the D4 pipeline and turn the <em>outcome</em> into the documented response — including the
/// cookie work, which is the Api's job because only the Api knows about cookies (09.3.2-T1 Design).
/// </para>
/// <para>
/// Two rules are shared by all three routes: the success body carries the access token and its expiry and
/// never the refresh token (that one only ever travels in the cookie), and every auth response is
/// <c>Cache-Control: no-store</c>, so no proxy can keep a token body it happened to see.
/// </para>
/// <para>
/// The <c>Origin</c> filter is on refresh and logout — the two cookie-authenticated POSTs — and not on
/// login, which carries no cookie yet and is bounded by the rate-limit policy instead (BD3/BD16).
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>The group's prefix; every auth route of 09.3 hangs under it.</summary>
    public const string GroupPrefix = "/api/v1/auth";

    /// <summary>The rate-limit policy every auth route carries (owned by 09.3.5-T1, 09 README BD14).</summary>
    /// <remarks>
    /// Referenced by name only: until 09.3.5-T1 registers the policy the metadata is inert, so auth
    /// traffic is unlimited — the alternative (a policy defined here) would create a second owner of the
    /// registered limit.
    /// </remarks>
    public const string AuthRateLimitPolicy = "auth";

    /// <summary>Maps the auth routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var auth = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the credentialed policy is for these routes only — the refresh cookie needs
        // `Access-Control-Allow-Credentials` (BD3) and no other surface does.
        auth.RequireCors(SecurityHeadersExtensions.AuthCorsPolicy);

        auth.MapPost(
                "/register",
                async (RegisterCommand command, ISender sender, HttpContext http, CancellationToken ct) =>
                    (await sender.Send(command, ct).ConfigureAwait(false)).ToAcceptedResult(http))
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy);

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .Produces<LoginResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // 09.3.2-T3 (BD4): the second step of the login. It is anonymous like the first one and carries the
        // same rate-limit policy, because it is the credential surface a wrong code is guessed on. It needs
        // no `RequireSameOriginFilter`: the request authenticates with the body's single-use `mfaToken`,
        // which a cross-site caller cannot read, so there is no cookie-only CSRF to defend against here.
        auth.MapPost("/login/mfa", LoginMfaAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .Produces<LoginResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .AddEndpointFilter<RequireSameOriginFilter>()
            .Produces<RefreshResponse>(StatusCodes.Status200OK);

        auth.MapPost("/logout", LogoutAsync)
            .RequireRateLimiting(AuthRateLimitPolicy)
            .AddEndpointFilter<RequireSameOriginFilter>();

        // 09.10.4-T1 (BD4): re-authentication is a credential route, so it carries the auth rate limit.
        // 09.3.2-T3: the shared WT35 window can also answer here, so the 429 is documented like /login's.
        auth.MapPost("/reauth", ReauthAsync)
            .RequireRateLimiting(AuthRateLimitPolicy)
            .Produces<ReauthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/auth/login</c>: 200 with the access token and the refresh cookie, or the
    /// documented 401/403 problem.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        LoginCommand command,
        ISender sender,
        HttpContext http,
        IOptions<SessionOptions> sessionOptions,
        IClock clock,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct).ConfigureAwait(false);
        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;

        return value.Outcome switch
        {
            LoginOutcome.Tokens => WriteLoginSuccess(
                http,
                sessionOptions.Value,
                value.Access!,
                value.Refresh!,
                value.Role,
                value.Amr,
                clock.UtcNow),
            LoginOutcome.MfaRequired => WriteMfaRequired(http, value.Challenge!, clock.UtcNow),
            LoginOutcome.RateLimited => ProblemDetailsWriter.ToProblem(
                [MfaErrors.RateLimited(InMemoryMfaGuessWindow.RetryAfterSeconds)],
                http),
            _ => Problem(
                http,
                ErrorCodes.MfaEnrolmentRequired,
                "This account must enrol a second factor before it can sign in."),
        };
    }

    /// <summary>
    /// <c>POST /api/v1/auth/login/mfa</c>: 200 with the access token and the refresh cookie, or the
    /// documented 401/429 problem.
    /// </summary>
    /// <remarks>
    /// The pre-auth cookie is read here, never accepted from the body: the command's fourth member is the
    /// Api's own contribution, exactly as <c>/auth/refresh</c> reads its cookie and passes the token. A
    /// missing cookie short-circuits to the same 401 <c>mfa_token_invalid</c> the store answers for a token
    /// it does not know, so the caller learns nothing about which half failed (BD4).
    /// </remarks>
    private static async Task<IResult> LoginMfaAsync(
        LoginMfaRequest body,
        ISender sender,
        HttpContext http,
        IOptions<SessionOptions> sessionOptions,
        IClock clock,
        CancellationToken ct)
    {
        var preAuthCookie = MfaPreAuthCookie.Read(http.Request);
        if (preAuthCookie is null)
        {
            return Problem(
                http,
                ErrorCodes.MfaTokenInvalid,
                "The sign-in challenge is no longer valid; sign in again.");
        }

        var result = await sender.Send(
            new LoginMfaCommand(body.MfaToken, body.Code, body.RecoveryCode, preAuthCookie),
            ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;

        if (value.Outcome is not MfaOutcome.Tokens)
        {
            // The outcome carries its own error — 401 invalid_mfa_code, 401 mfa_token_invalid or the 429
            // whose Retry-After the writer mirrors. The fallback is fail-closed if that ever stops holding.
            return ProblemDetailsWriter.ToProblem(
                [value.Failure ?? MfaErrors.MfaTokenInvalid()],
                http);
        }

        // The challenge is spent, so its cookie must not be resent.
        MfaPreAuthCookie.Clear(http.Response);

        return WriteLoginSuccess(
            http,
            sessionOptions.Value,
            value.Access!,
            value.Refresh!,
            value.Role,
            value.Method,
            clock.UtcNow);
    }

    /// <summary>
    /// <c>POST /api/v1/auth/refresh</c>: 200 with a fresh access token and the rotated cookie, or the 401
    /// the rotation's outcome maps to.
    /// </summary>
    /// <remarks>
    /// There is no request body: the token is the cookie, which is the Api layer's business (BD3). A
    /// missing cookie short-circuits to the same 401 <c>session_revoked</c> the store answers for an
    /// unknown one (WT21), so "no cookie" and "cookie nobody knows" are indistinguishable.
    /// </remarks>
    private static async Task<IResult> RefreshAsync(
        HttpContext http,
        ISender sender,
        IOptions<SessionOptions> sessionOptions,
        IClock clock,
        CancellationToken ct)
    {
        var cookieName = sessionOptions.Value.Refresh.CookieName;
        var presented = http.Request.Cookies[cookieName];

        if (string.IsNullOrWhiteSpace(presented))
        {
            RefreshCookie.Clear(http.Response, cookieName);
            return Problem(http, ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.");
        }

        var result = await sender.Send(new RefreshCommand(presented), ct).ConfigureAwait(false);
        if (result.IsError)
        {
            // The rotation rolled back (an unconfirmed address); the cookie stays as it was, so the user can
            // verify the address and keep the session (09.3.2-T1 Design).
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;

        switch (value.Outcome)
        {
            case RefreshOutcome.Tokens:
                NoStore(http);
                RefreshCookie.Write(
                    http.Response,
                    cookieName,
                    value.Refresh!.Token,
                    value.Refresh.ExpiresAt,
                    clock.UtcNow);

                return Results.Json(
                    new RefreshResponse(value.Access!.Token, value.Access.ExpiresAt),
                    statusCode: StatusCodes.Status200OK);

            case RefreshOutcome.ReuseDetected:
                // The family is revoked, so the cookie the caller still holds is dead: clearing it saves the
                // client one doomed round trip.
                RefreshCookie.Clear(http.Response, cookieName);
                return Problem(
                    http,
                    ErrorCodes.RefreshTokenReused,
                    "The refresh token was already used; the session family was revoked.");

            default:
                RefreshCookie.Clear(http.Response, cookieName);
                return Problem(http, ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.");
        }
    }

    /// <summary>
    /// <c>POST /api/v1/auth/logout</c>: 204 with the cookie cleared, or 401 <c>session_revoked</c> for an
    /// unknown cookie — which is cleared as well.
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        ISender sender,
        IOptions<SessionOptions> sessionOptions,
        CancellationToken ct)
    {
        var cookieName = sessionOptions.Value.Refresh.CookieName;
        var presented = http.Request.Cookies[cookieName];

        // Whatever the handler answers, the caller is not left holding a cookie the API does not accept.
        ErrorOr<Success> result = string.IsNullOrWhiteSpace(presented)
            ? Error.Unauthorized(ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.")
            : await sender.Send(new LogoutCommand(presented), ct).ConfigureAwait(false);

        RefreshCookie.Clear(http.Response, cookieName);

        return result.IsError
            ? ProblemDetailsWriter.ToProblem(result.Errors, http)
            : Results.NoContent();
    }

    /// <summary>
    /// <c>POST /api/v1/auth/reauth</c>: 200 with the instant the family was stamped, or the 401 the
    /// failure's own error maps to ([RULED — README §B10 WT29]).
    /// </summary>
    /// <remarks>
    /// A failure arrives as a <em>value</em>, not as an error: the counter, the possible family
    /// revocation and the audit row must commit before the caller is told 401, and an error result would
    /// roll them back. The error the flow carries is mapped here, unchanged.
    /// </remarks>
    private static async Task<IResult> ReauthAsync(
        ReauthCommand command,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        NoStore(http);

        var value = result.Value;

        if (value.Outcome is ReauthOutcome.Succeeded)
        {
            return Results.Json(
                new ReauthResponse(value.ReauthenticatedAt!.Value),
                statusCode: StatusCodes.Status200OK);
        }

        // A failure always carries its error; the fallback is fail-closed if that ever stops holding.
        var failure = value.Failure
            ?? Error.Unauthorized(ErrorCodes.SessionRevoked, "The session is no longer valid; sign in again.");

        return ProblemDetailsWriter.ToProblem([failure], http);
    }

    /// <summary>
    /// Writes the 401 <c>mfa_required</c> step: the pre-auth cookie plus the body that carries the
    /// single-use challenge token.
    /// </summary>
    private static IResult WriteMfaRequired(HttpContext http, MfaChallenge challenge, DateTimeOffset now)
    {
        NoStore(http);

        MfaPreAuthCookie.Write(
            http.Response,
            challenge.PreAuthCookie,
            challenge.ExpiresAt,
            now);

        return ProblemDetailsWriter.MfaRequired(http, challenge.MfaToken, "A one-time code is required.");
    }

    /// <summary>The one success body of <c>/auth/login</c> and <c>/auth/login/mfa</c>: identical by contract.</summary>
    private static IResult WriteLoginSuccess(
        HttpContext http,
        SessionOptions sessionOptions,
        IssuedAccessToken access,
        IssuedRefreshSession refresh,
        PlatformRole role,
        AuthMethod amr,
        DateTimeOffset now)
    {
        NoStore(http);

        RefreshCookie.Write(
            http.Response,
            sessionOptions.Refresh.CookieName,
            refresh.Token,
            refresh.ExpiresAt,
            now);

        return Results.Json(
            new LoginResponse(
                access.Token,
                access.ExpiresAt,
                RoleWire(role),
                [amr.ToAmrClaim()]),
            statusCode: StatusCodes.Status200OK);
    }

    private static IResult Problem(HttpContext http, string code, string description) =>
        ProblemDetailsWriter.ToProblem([Error.Unauthorized(code, description)], http);

    /// <summary>The token body carries no <c>refresh_token_reused</c> case, so every problem here is a 401.</summary>
    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    /// <summary>The snake_case <c>role</c> of the wire contract (10-web-ui's <c>AuthRole</c>).</summary>
    private static string RoleWire(PlatformRole role) => role.ToString().ToLowerInvariant();
}

/// <summary>
/// The frozen <c>POST /auth/login</c> and <c>POST /auth/login/mfa</c> body
/// (09.3-auth plan §5, 10-web-ui 10.3.3 <c>Session</c>): the access token, when it dies, the role and how
/// the caller proved who they are — never the refresh token, which only the cookie carries.
/// </summary>
/// <param name="AccessToken">The ES256 access token; never logged.</param>
/// <param name="ExpiresAt">When it dies, from the server's clock.</param>
/// <param name="Role">One of <c>user</c>, <c>admin</c>, <c>owner</c>.</param>
/// <param name="Amr">The authentication methods behind the token (<c>pwd</c>, <c>otp</c> or <c>rc</c>).</param>
public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string Role,
    IReadOnlyList<string> Amr);

/// <summary>
/// The frozen <c>POST /auth/login/mfa</c> body (09.3-auth plan §5, 10.3.3-T3): the challenge token of the
/// password step and exactly one of the two codes.
/// </summary>
/// <remarks>
/// The pre-auth cookie is deliberately absent: the Api reads <c>__Secure-nts_mfa</c> and hands the value
/// to <c>LoginMfaCommand</c>, so a client cannot submit its own and a captured body is useless without
/// the browser's cookie (BD4).
/// </remarks>
/// <param name="MfaToken">The single-use token the <c>mfa_required</c> body carried; never logged.</param>
/// <param name="Code">The six-digit authenticator code, or null when <paramref name="RecoveryCode"/> is given.</param>
/// <param name="RecoveryCode">The recovery code, or null when <paramref name="Code"/> is given.</param>
public sealed record LoginMfaRequest(string MfaToken, string? Code, string? RecoveryCode);

/// <summary>
/// The frozen <c>POST /auth/reauth</c> body (09.3-auth plan §5, 10-web-ui 10.3.3): when the caller's
/// session was re-authenticated, so the SPA can re-arm the sensitive actions it gates.
/// </summary>
/// <param name="ReauthenticatedAt">The instant the family's re-authentication was stamped.</param>
public sealed record ReauthResponse(DateTimeOffset ReauthenticatedAt);

/// <summary>
/// The frozen <c>POST /auth/refresh</c> body (09.3-auth plan §5, 10.2.1-T1's <c>TokenPair</c>).
/// </summary>
/// <param name="AccessToken">The fresh ES256 access token; never logged.</param>
/// <param name="ExpiresAt">When it dies, from the server's clock.</param>
public sealed record RefreshResponse(string AccessToken, DateTimeOffset ExpiresAt);
