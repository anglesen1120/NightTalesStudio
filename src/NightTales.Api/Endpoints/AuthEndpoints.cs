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
using NightTales.Application.Features.Auth.Logout.Commands;
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
            .Produces<LoginResponse>(StatusCodes.Status200OK);

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .AddEndpointFilter<RequireSameOriginFilter>()
            .Produces<RefreshResponse>(StatusCodes.Status200OK);

        auth.MapPost("/logout", LogoutAsync)
            .RequireRateLimiting(AuthRateLimitPolicy)
            .AddEndpointFilter<RequireSameOriginFilter>();

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
            LoginOutcome.Tokens => WriteLoginSuccess(http, sessionOptions.Value, value, clock.UtcNow),
            LoginOutcome.MfaRequired => Problem(http, ErrorCodes.MfaRequired, "A one-time code is required."),
            _ => Problem(
                http,
                ErrorCodes.MfaEnrolmentRequired,
                "This account must enrol a second factor before it can sign in."),
        };
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

    private static IResult WriteLoginSuccess(
        HttpContext http,
        SessionOptions sessionOptions,
        LoginResult value,
        DateTimeOffset now)
    {
        NoStore(http);

        RefreshCookie.Write(
            http.Response,
            sessionOptions.Refresh.CookieName,
            value.Refresh!.Token,
            value.Refresh.ExpiresAt,
            now);

        return Results.Json(
            new LoginResponse(
                value.Access!.Token,
                value.Access.ExpiresAt,
                RoleWire(value.Role),
                [value.Amr.ToAmrClaim()]),
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
/// The frozen <c>POST /auth/refresh</c> body (09.3-auth plan §5, 10.2.1-T1's <c>TokenPair</c>).
/// </summary>
/// <param name="AccessToken">The fresh ES256 access token; never logged.</param>
/// <param name="ExpiresAt">When it dies, from the server's clock.</param>
public sealed record RefreshResponse(string AccessToken, DateTimeOffset ExpiresAt);
