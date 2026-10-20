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
using NightTales.Application.Features.Publishing.Channels.Commands.DisconnectChannel;
using NightTales.Application.Features.Publishing.Channels.Queries.ListChannels;
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
    /// The disconnect route of 06.4.1-T1, relative to <see cref="GroupPrefix"/>: the connection id, never the platform
    /// — a disconnect names one connection the caller owns, not a platform's connection.
    /// </summary>
    public const string DisconnectRoute = "/{id:guid}";

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

    /// <summary>
    /// How long the callback's post-claim work may run (P6): the flow runs on a token of its own instead of
    /// the request's <c>RequestAborted</c>, so a browser that disconnects after Google spent the code does
    /// not cancel the exchange and drop a live grant. Two 15-second Google calls plus the claim fit well
    /// inside it.
    /// </summary>
    public static readonly TimeSpan PostClaimTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The one line of an unexpected callback failure: the exception's <em>type</em> only. Its message may
    /// carry a code, a token or a statement, and the redirect is the same fixed answer either way (R5).
    /// </summary>
    private static readonly Action<ILogger, string, Exception?> CallbackUnhandled = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(6401, "publishing.callback.unhandled"),
        "publishing.callback.unhandled exceptionType={ExceptionType}");

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

        // 06.1.2-T2 (06 README D4/D6): the account's own connections — the one source the channels page reads.
        // Authenticated and flag-gated like the connect route, with no re-auth (it reads, it does not write) and
        // no idempotency key or If-Match (no state changes, D15).
        channels.MapGet(string.Empty, ListChannelsAsync)
            .RequireFeature(FeatureKey)
            .RequireAuthorization()
            .Produces<ChannelListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        channels.MapGet(CallbackRoute, CompleteConnectAsync)
            .RequireFeature(FeatureKey)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // 06.4.1-T1 (06 README D15/D17/D18): the disconnect — the owner's own destructive intent on a connection the
        // path names, so no If-Match and no idempotency key (D15: the repeat answers 204 again, and the locked read,
        // not a stale tag, decides). Rate-limited like the connect route because it is the one channel route that
        // reaches Google.
        channels.MapDelete(DisconnectRoute, DisconnectAsync)
            .RequireFeature(FeatureKey)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    /// <summary>
    /// <c>DELETE /api/v1/channels/{id}</c>: 204 once the connection is revoked, its open publications are cancelled and
    /// its deletion request is scheduled — or 404 for an unknown or foreign id.
    /// </summary>
    /// <remarks>
    /// 204 for the repeat too (D15, R3): a retried request after a lost response answers exactly what the first attempt
    /// answered. The route carries no body, no If-Match and no idempotency key — the command's locked read is the only
    /// guard.
    /// </remarks>
    /// <param name="id">The connection id from the path.</param>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="http">The request, for the problem details of a refusal.</param>
    /// <param name="ct">The request cancellation token.</param>
    private static async Task<IResult> DisconnectAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new DisconnectChannelCommand(id), ct).ConfigureAwait(false)).ToNoContentResult(http);

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
    /// <c>GET /api/v1/channels</c>: 200 with <c>{ items: [...] }</c> — the caller's own connections, newest
    /// first — or the mapped error problem when the query fails (06.1.2-T2 task §Endpoint, 06 README D4/D6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One tenant-filtered statement in <c>ListChannelsHandler</c>; this method only wraps the list in the
    /// <see cref="ChannelListResponse"/> envelope and writes it. The envelope is built here, not in the
    /// Application layer: <c>items</c> is 09 README D6's wire convention, and the query answers the DTO list
    /// itself.
    /// </para>
    /// <para>
    /// No ETag, no <c>If-Match</c>, no paging (D15/D6): the resource is read-only here, and a user has a handful
    /// of connections. An authenticated caller with the flag off answers 404 <c>feature_disabled</c> — the
    /// feature filter runs before the handler, and <c>RequireAuthorization</c> answers 401 for an anonymous one.
    /// </para>
    /// </remarks>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="http">The request, for the problem details of a failed query.</param>
    /// <param name="ct">The request cancellation token.</param>
    private static async Task<IResult> ListChannelsAsync(ISender sender, HttpContext http, CancellationToken ct)
    {
        var result = await sender
            .Send(new ListChannelsQuery(), ct)
            .ConfigureAwait(false);

        return result.IsError
            ? result.ToHttpResult(http)
            : Results.Json(new ChannelListResponse(result.Value), statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>GET /api/v1/channels/{platform}/callback?code&amp;state&amp;error</c>: 302 to one of the two fixed
    /// relative paths, or 404 <c>feature_disabled</c>/<c>not_found</c>, or 429 <c>rate_limited</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The nonce cookie is cleared and the two headers are set <em>before</em> the handler runs, so every
    /// outcome of this endpoint carries them — the successful connect, each of the four failures and a
    /// failure that throws (R5/F3). The cookie is read first (it is the handler's second binding), so the
    /// read and the clear can never disagree (YR10).
    /// </para>
    /// <para>
    /// <c>Referrer-Policy: no-referrer</c> is stated explicitly even though 09.9.4-T1's hardening headers
    /// (BD12) already carry it on every response: the callback's URL holds the single-use <c>code</c> and
    /// the <c>state</c>, so the requirement is part of this route's contract and must survive a change to
    /// the global defaults. <c>Cache-Control: no-store</c> likewise keeps the redirect out of every cache.
    /// </para>
    /// <para>
    /// An <c>ErrorOr</c> error is answered as a redirect, never as a 500: a browser navigation cannot
    /// render a ProblemDetails body. The handler answers its four non-connected outcomes as errors, and
    /// <see cref="FailureOutcome"/> maps each code to its own constant target; every other code — a commit
    /// mapping of either unit of work — answers the generic exchange failure. No target carries the
    /// pipeline's error text, and the failure is already logged by <c>LoggingBehavior</c>.
    /// </para>
    /// <para>
    /// A thrown exception is caught here for the same reason: the state is already spent, so the browser must
    /// still land on the fixed failure redirect rather than on a 500 body. Only the exception's type is
    /// logged. <see cref="OperationCanceledException"/> is rethrown only when the host is stopping or the
    /// caller aborted, where nobody is left to read a redirect; the bounded post-claim timeout, whose browser
    /// is still waiting, takes the same fixed-redirect branch as any other failure (R5, N2).
    /// </para>
    /// </remarks>
    /// <param name="platform">The platform from the route; only <c>youtube</c> exists in P (07 owns TikTok, S).</param>
    /// <param name="code">Google's authorization code, or null when Google refused or the user cancelled.</param>
    /// <param name="state">The single-use state minted by connect, or null.</param>
    /// <param name="error">Google's <c>error</c> query value (<c>access_denied</c> when the user refused), or null.</param>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="requestInfo">The caller's untrusted IP/User-Agent, for the audit row of the success path.</param>
    /// <param name="lifetime">The host's shutdown token, so the bounded post-claim token still honours a stop.</param>
    /// <param name="http">The request, for the nonce cookie and the response headers.</param>
    private static async Task<IResult> CompleteConnectAsync(
        string platform,
        [FromQuery(Name = "code")] string? code,
        [FromQuery(Name = "state")] string? state,
        [FromQuery(Name = "error")] string? error,
        ISender sender,
        IRequestInfoAccessor requestInfo,
        IHostApplicationLifetime lifetime,
        HttpContext http)
    {
        // 06.1 plan §5.1: a platform other than `youtube` is the same 404 a missing route answers. The
        // parsed value also travels into the command, where it is the claim's second predicate, so a state
        // minted for another platform can never be completed through this route (F8).
        if (!PlatformExtensions.TryParse(platform, out var routePlatform))
        {
            return ProblemDetailsWriter.ToProblem([Errors.NotFound()], http);
        }

        var nonce = OAuthNonceCookie.Read(http.Request);

        // The two headers and the clear are written before the handler runs, so every outcome of the flow
        // carries them — the successful connect, each of the four failures, and one that throws (R5/F3). The
        // state is single-use and already spent by then, so leaving the nonce for its ten-minute Max-Age
        // would only let the browser present a cookie that can never complete a connect again.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        OAuthNonceCookie.Clear(http.Response);

        // The post-claim work runs on a token of its own (P6): a browser that disconnects while Google is
        // answering must not cancel the exchange, because the code is already spent and the grant Google
        // just minted would be dropped un-stored. Application shutdown still wins, and the timeout bounds a
        // stuck Google call.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        bounded.CancelAfter(PostClaimTimeout);

        ErrorOr<ConnectYouTubeOutcome> result;
        try
        {
            result = await sender
                .Send(
                    new CompleteYouTubeConnectCommand(routePlatform, code, state, error, nonce, requestInfo.Current),
                    bounded.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsGoingAway(lifetime, http))
        {
            // Shutdown, or the browser navigating away: nobody is left to receive the fixed redirect, and the
            // pipeline has already rolled unit of work #2 back. This is the only cancellation that is
            // rethrown. The bounded PostClaimTimeout is NOT this case — the browser is still waiting there, so
            // it falls into the branch below and gets the fixed failure redirect.
            throw;
        }
        catch (Exception exception)
        {
            // By type only (09 README D17, FetcherEndpoints precedent): the exception's message may hold a
            // URL, a statement or a token response, so the record never carries it (R5). The bounded
            // PostClaimTimeout lands here too, and the state is already spent either way: the fixed failure
            // redirect is the honest answer, with the cookie and the two headers already on the response.
            CallbackUnhandled(
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("NightTales.Api.Publishing.Callback"),
                exception.GetType().Name,
                null);

            return Results.Redirect(ErrorRedirectPrefix + ErrorCodes.OauthExchangeFailed);
        }

        // The handler's own answer, never a blind `Connected` (N1): a member other than Connected would map to
        // its own constant arm below rather than silently reading as a successful connect. Today the handler
        // returns Connected alone, which the arch rule pins.
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
    /// Whether the cancellation that ended a callback came from the host stopping or from the caller going
    /// away: only then is there nobody left to receive a redirect, so only then is the exception rethrown. The
    /// bounded post-claim timeout is a flow outcome instead, because its browser is still waiting (N2).
    /// </summary>
    /// <param name="lifetime">The host's lifetime.</param>
    /// <param name="http">The request.</param>
    private static bool IsGoingAway(IHostApplicationLifetime lifetime, HttpContext http) =>
        lifetime.ApplicationStopping.IsCancellationRequested || http.RequestAborted.IsCancellationRequested;

    /// <summary>
    /// The outcome of the <c>ErrorOr</c> error the handler (or the pipeline's own commit mapping) returned:
    /// the handler's four failure codes each name their own member, and every other code — a
    /// <c>concurrency_conflict</c> from unit #2's commit, a <c>service_unavailable</c> from unit #1's, an
    /// unmapped provider failure — is the generic exchange failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler answers <c>oauth_state_invalid</c>, <c>oauth_denied</c>, <c>oauth_exchange_failed</c> and
    /// <c>channel_already_connected</c> for the four non-connected outcomes, so this split is what turns an
    /// error back into the enum the redirect set is expressed in. Every target below is a constant of that
    /// set, so the distinction leaks nothing: the state answer is already indistinguishable from any other
    /// unclaimable state on the wire (YR10), and every other failure reads as "Google did not complete the
    /// connection".
    /// </para>
    /// <para>
    /// <c>concurrency_conflict</c> is deliberately <em>not</em> mapped to <see
    /// cref="ConnectYouTubeOutcome.StateInvalid"/> (R6): its only real source is unit #2's commit — a
    /// reconnect race against the T2 writer, or the active-run mapping of <c>EfUnitOfWork</c> — where the
    /// state was valid and has already been spent, so telling the user the link was invalid would be a lie.
    /// </para>
    /// </remarks>
    private static ConnectYouTubeOutcome FailureOutcome(List<Error> errors) =>
        errors is { Count: > 0 }
            ? errors[0].Code switch
            {
                ErrorCodes.OauthStateInvalid => ConnectYouTubeOutcome.StateInvalid,
                ErrorCodes.OauthDenied => ConnectYouTubeOutcome.Denied,
                ErrorCodes.ChannelAlreadyConnected => ConnectYouTubeOutcome.ChannelAlreadyConnected,
                _ => ConnectYouTubeOutcome.ExchangeFailed,
            }
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
