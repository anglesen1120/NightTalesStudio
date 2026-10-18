using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Contracts.Publishing;
using NightTales.Api.Http;
using NightTales.Api.Http.Cookies;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;
using NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Api.Endpoints.Publishing;

/// <summary>
/// The channel routes of 06.1.2-T1 (06 README D3, 06.1 connect plan §5.1): <c>POST
/// /api/v1/channels/{platform}/connect</c> starts the Google authorization-code flow and <c>GET
/// /api/v1/channels/{platform}/callback</c> finishes it.
/// </summary>
/// <remarks>
/// <para>
/// Both routes are thin: they bind, send one message through the D4 pipeline and turn the outcome into the
/// documented response. The state's entropy, the PKCE verifier, the claiming UPDATE, the token exchange
/// and the audit row belong to the handlers; the cookie, the <c>Location</c> and the two headers belong
/// here, because a handler has no <c>HttpResponse</c> (09 README D14).
/// </para>
/// <para>
/// The callback is a top-level browser navigation from Google, so it carries no bearer token: it
/// authenticates through the single-use <c>state</c> claimed by its handler, the <c>__Host-nts_oauth</c>
/// nonce cookie and the session family — all three on the same row (F01, YR10). It answers 302 for every
/// flow outcome, including the ones the other routes answer 401/409 for, because a browser cannot render a
/// ProblemDetails body usefully; its failure <c>Location</c> values are constants and no <c>code</c>,
/// <c>state</c> or <c>error</c> value is ever interpolated into them. <c>RequireSameOriginFilter</c> is
/// deliberately absent: the callback arrives cross-site by design and <c>SameSite=Lax</c> is what lets it
/// carry the cookie, so the state plus the nonce plus the family liveness are the CSRF defence instead.
/// </para>
/// <para>
/// Filter order: the feature guard is attached first, so a route whose flag is off answers 404
/// <c>feature_disabled</c> without running the re-auth filter or the handler. Authorization and the
/// rate-limit policy are middleware and run before every endpoint filter — an anonymous caller on the
/// connect route therefore answers 401 before the flag is read, exactly as
/// <see cref="YouTubeEtaEndpoints"/> documents (09 README BD13's guard is a route filter, not middleware).
/// The callback is <c>AllowAnonymous</c>, so the flag answer is the first thing it can observe.
/// </para>
/// <para>
/// Neither route requires an <c>Idempotency-Key</c>: §5.4's rule is for paid/platform POSTs, <c>connect</c>
/// mints a fresh state per call on purpose (bounded by the <c>auth</c> policy) and the callback's single-use
/// state is its own idempotency guard (06.1 plan §5.1).
/// </para>
/// </remarks>
public static class PublishingEndpoints
{
    /// <summary>The group's prefix; the channel routes of 06 hang under it.</summary>
    public const string GroupPrefix = "/api/v1/channels";

    /// <summary>The connect route, relative to <see cref="GroupPrefix"/>.</summary>
    public const string ConnectRoute = "/{platform}/connect";

    /// <summary>The callback route, relative to <see cref="GroupPrefix"/>.</summary>
    public const string CallbackRoute = "/{platform}/callback";

    /// <summary>
    /// The feature flag that gates every YouTube publishing route (06 README D9); the same key
    /// <see cref="YouTubeEtaEndpoints"/> carries, declared once there.
    /// </summary>
    public const string FeatureKey = YouTubeEtaEndpoints.FeatureKey;

    /// <summary>The <c>Location</c> of a connect that succeeded (06.1 plan §5.1) — a constant.</summary>
    public const string ConnectedRedirect = "/channels?connected=youtube";

    /// <summary>
    /// The prefix of the <c>Location</c> of a connect that failed; only the catalogue's own constants are
    /// appended to it, never caller input (README D3).
    /// </summary>
    public const string ErrorRedirectPrefix = "/channels?error=";

    /// <summary>The re-auth window the connect route requires (A5: 5 minutes, 06.1 plan UC1).</summary>
    public static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5);

    /// <summary>Maps the channel routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapPublishingEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var channels = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the connect route is called by the SPA with a bearer token, so its preflight
        // needs the versioned API's non-credentialed policy — the nonce and the state travel in the body
        // and in the cookie of a same-site response, never as CORS credentials.
        channels.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        channels.MapPost(ConnectRoute, ConnectAsync)
            .RequireFeature(FeatureKey)
            .RequireReauth(ReauthWindow)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<ConnectChannelResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        channels.MapGet(CallbackRoute, CompleteConnectAsync)
            .RequireFeature(FeatureKey)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/channels/{platform}/connect</c>: 200 with the authorization URL and the state's
    /// expiry, or the documented 401 <c>unauthorized</c>/<c>reauth_required</c>, 404
    /// <c>feature_disabled</c>/<c>not_found</c>, 409 <c>legal_acceptance_required</c>, 429
    /// <c>rate_limited</c>, 503 <c>service_unavailable</c>.
    /// </summary>
    /// <remarks>
    /// The handler's response carries the nonce as a third member; it is written to the
    /// <c>__Host-nts_oauth</c> cookie and never reaches <see cref="ConnectChannelResponse"/>. The response
    /// is <c>no-store</c> for the same reason the auth responses are: its body contains a one-time
    /// credential (the <c>state</c>) that no proxy or browser cache may keep.
    /// </remarks>
    /// <param name="platform">The platform from the route; the validator answers 404 for anything but <c>youtube</c>.</param>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="clock">The scope's clock, so the cookie's <c>Max-Age</c> matches the state's own expiry.</param>
    /// <param name="http">The request, for the response headers and the cookie.</param>
    /// <param name="ct">The request cancellation token.</param>
    private static async Task<IResult> ConnectAsync(
        string platform,
        ISender sender,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender
            .Send(new ConnectYouTubeCommand(platform), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return MapConnectFailure(result.Errors, http);
        }

        var response = result.Value;

        http.Response.Headers.CacheControl = "no-store";
        OAuthNonceCookie.Write(http.Response, response.BrowserNonce, response.ExpiresAt, clock.UtcNow);

        return Results.Json(
            new ConnectChannelResponse(response.AuthorizationUrl, response.ExpiresAt),
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>GET /api/v1/channels/{platform}/callback?code&amp;state&amp;error</c>: 302 to one of the two fixed
    /// relative paths, or 404 <c>feature_disabled</c>/<c>not_found</c>, or 429 <c>rate_limited</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The nonce cookie is cleared on every flow outcome — the successful connect and each of the four
    /// failures — before the redirect is built, and the cookie is read before the handler runs (it is the
    /// handler's second binding), so the read and the clear can never disagree (YR10).
    /// </para>
    /// <para>
    /// <c>Referrer-Policy: no-referrer</c> is stated explicitly even though 09.9.4-T1's hardening headers
    /// (BD12) already carry it on every response: the callback's URL holds the single-use <c>code</c> and
    /// the <c>state</c>, so the requirement is part of this route's contract and must survive a change to
    /// the global defaults. <c>Cache-Control: no-store</c> likewise keeps the redirect out of every cache.
    /// </para>
    /// <para>
    /// An <c>ErrorOr</c> error inside the pipeline is answered as a redirect, never as a 500: a browser
    /// navigation cannot render a ProblemDetails body. Two buckets are distinguished, both inside the
    /// closed redirect set — the claiming UPDATE's own refusals (<c>oauth_state_invalid</c>, and the
    /// <c>concurrency_conflict</c> a race between two Api instances produces, where the loser holds no
    /// state exactly as a replayed one does) answer the generic "expired or already used" target, and
    /// every other pipeline failure — a commit mapping of unit of work #2 — answers the generic exchange
    /// failure. Neither target carries the pipeline's error text, and the failure is already logged by
    /// <c>LoggingBehavior</c> (its <c>outcome</c> member is the first error code).
    /// </para>
    /// </remarks>
    /// <param name="platform">The platform from the route; only <c>youtube</c> exists in P (07 owns TikTok, S).</param>
    /// <param name="code">Google's authorization code, or null when Google refused or the user cancelled.</param>
    /// <param name="state">The single-use state minted by connect, or null.</param>
    /// <param name="error">Google's <c>error</c> query value (<c>access_denied</c> when the user refused), or null.</param>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="requestInfo">The caller's untrusted IP/User-Agent, for the audit row of the success path.</param>
    /// <param name="http">The request, for the nonce cookie and the response headers.</param>
    /// <param name="ct">The request cancellation token.</param>
    private static async Task<IResult> CompleteConnectAsync(
        string platform,
        [FromQuery(Name = "code")] string? code,
        [FromQuery(Name = "state")] string? state,
        [FromQuery(Name = "error")] string? error,
        ISender sender,
        IRequestInfoAccessor requestInfo,
        HttpContext http,
        CancellationToken ct)
    {
        // 06.1 plan §5.1: a platform other than `youtube` is the same 404 a missing route answers — the
        // route value is checked here because the command carries no platform (the state row has it).
        if (!PlatformExtensions.TryParse(platform, out _))
        {
            return ProblemDetailsWriter.ToProblem([Errors.NotFound()], http);
        }

        var nonce = OAuthNonceCookie.Read(http.Request);

        var result = await sender
            .Send(
                new CompleteYouTubeConnectCommand(code, state, error, nonce, requestInfo.Current),
                ct)
            .ConfigureAwait(false);

        OAuthNonceCookie.Clear(http.Response);

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";

        var outcome = result.IsError ? FailureOutcome(result.Errors) : result.Value;

        return Results.Redirect(outcome switch
        {
            ConnectYouTubeOutcome.Connected => ConnectedRedirect,
            ConnectYouTubeOutcome.StateInvalid => ErrorRedirectPrefix + ErrorCodes.OauthStateInvalid,
            ConnectYouTubeOutcome.Denied => ErrorRedirectPrefix + ErrorCodes.OauthDenied,
            ConnectYouTubeOutcome.ChannelAlreadyConnected => ErrorRedirectPrefix + ErrorCodes.ChannelAlreadyConnected,
            _ => ErrorRedirectPrefix + ErrorCodes.OauthExchangeFailed,
        });
    }

    /// <summary>
    /// The outcome of an <c>ErrorOr</c> error the pipeline returned instead of one of the five
    /// success-typed values: the claim's own refusals keep the state bucket, every other failure is the
    /// generic exchange failure.
    /// </summary>
    /// <remarks>
    /// <c>CompleteYouTubeConnectHandler</c> is success-typed, so an error means the pipeline refused the
    /// call rather than the flow failing at Google: unit of work #1's commit mapped a concurrency or
    /// referential refusal (<c>oauth_state_invalid</c>, <c>concurrency_conflict</c> — the loser of a race
    /// between two Api instances holds no state, exactly like a replay), or unit of work #2's commit was
    /// mapped. Both targets are constants of the closed redirect set, so the distinction leaks nothing:
    /// the two state answers are already indistinguishable from each other on the wire (YR10), and the
    /// remaining failures all read as "Google did not complete the connection".
    /// </remarks>
    private static ConnectYouTubeOutcome FailureOutcome(List<Error> errors)
        => errors is { Count: > 0 } && errors[0].Code is ErrorCodes.OauthStateInvalid or ErrorCodes.ConcurrencyConflict
            ? ConnectYouTubeOutcome.StateInvalid
            : ConnectYouTubeOutcome.ExchangeFailed;

    /// <summary>
    /// Maps the connect handler's errors onto the plan's §5.2 wire: every pipeline error (401
    /// <c>reauth_required</c>, 404 <c>not_found</c>, 409 <c>legal_acceptance_required</c> with its
    /// <c>errors.documents</c>) travels unchanged, and the two errors of the OAuth-client resolution are
    /// normalised to the codes P catalogues.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>oauth_client_secret_unavailable</c> and <c>oauth_client_unknown</c> are the resolver's own
    /// <c>[INFERENCE]</c> codes (06.1.2-T1 §Infrastructure, 06 CR-17 adds the second one to the §5.4
    /// catalogue with the S-phase settings surface). Until that row exists, an un-normalised code would
    /// reach <see cref="ProblemDetailsWriter"/>'s unknown-code path and answer 500 <c>internal_error</c>,
    /// while §5.2 fixes the answer of an unreadable secret at 503 <c>service_unavailable</c> and the
    /// platform's 404 for anything the caller must not distinguish. The two literals below are the
    /// resolver's contract, quoted once, and the normalisation disappears with the CR-17 row.
    /// </para>
    /// <para>
    /// The replacement description is fixed here: the resolver's own message is server-side detail the
    /// client must not receive, and the secret (or its ref) is never in it either way.
    /// </para>
    /// </remarks>
    private static IResult MapConnectFailure(List<Error> errors, HttpContext http)
    {
        var mapped = errors.ConvertAll(error => error.Code switch
        {
            ResolverClientUnknownCode => Errors.NotFound(),
            ResolverClientSecretUnavailableCode => Error.Failure(
                ErrorCodes.ServiceUnavailable,
                OAuthClientUnavailableDescription),
            _ => error,
        });

        return ProblemDetailsWriter.ToProblem(mapped, http);
    }

    /// <summary>The resolver's code for a client id that resolves to no entry.</summary>
    private const string ResolverClientUnknownCode = "oauth_client_unknown";

    /// <summary>The resolver's code for a client whose secret cannot be read.</summary>
    private const string ResolverClientSecretUnavailableCode = "oauth_client_secret_unavailable";

    /// <summary>The client-safe 503 title; the resolver's own message stays server-side.</summary>
    private const string OAuthClientUnavailableDescription =
        "The YouTube OAuth client is not available; try again later.";
}
