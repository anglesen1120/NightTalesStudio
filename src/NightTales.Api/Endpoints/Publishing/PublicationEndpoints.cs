using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Contracts.Publishing;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Commands.ConsentPublication;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Application.Features.Publishing.Queries.GetConsentText;

namespace NightTales.Api.Endpoints.Publishing;

/// <summary>
/// The publication routes of 06.2.2-T1 (06 README D7, 10 CR-17): <c>POST /api/v1/publications/{id}/consent</c>
/// records the owner's consent on an <c>awaiting_consent</c> row, and <c>GET /api/v1/publications/consent-text</c>
/// serves the <c>pc.v1</c> text the consent dialogue displays.
/// </summary>
/// <remarks>
/// <para>
/// The file is deliberately not <see cref="EpisodePublicationEndpoints"/> (the episode's collection of 06.2.1-T1)
/// and not <see cref="PublishingEndpoints"/> (the <c>/api/v1/channels</c> routes of 06.1.2): both routes here hang
/// under the publication resource itself, at <c>/api/v1/publications</c>, which is neither of those groups nor the
/// episode one, so it gets its own group and its own file.
/// </para>
/// <para>
/// Both routes are thin (09.1.2-T1): bind, send one message through the D4 pipeline, answer. The idempotent repeat,
/// the <c>If-Match</c> comparison, the text-version rule, the episode transition and the audit/outbox rows belong to
/// the command handler; the <c>ETag</c> and the <c>Cache-Control</c> header belong here, because a handler has no
/// <c>HttpResponse</c> (09 README D14).
/// </para>
/// <para>
/// Both routes are behind <c>RequireFeature("youtube_publish")</c> (06 README D9), attached first so an authenticated
/// caller with the flag off answers 404 <c>feature_disabled</c> before the handler runs. Authorization is middleware
/// and runs before every endpoint filter, so an anonymous caller answers 401 first — including on the consent route,
/// where it precedes the 428 the <c>If-Match</c> filter would answer.
/// </para>
/// <para>
/// <b>Route order.</b> <c>consent-text</c> is a literal segment of the same group as the <c>{id:guid}</c> route; the
/// <c>guid</c> constraint is what keeps the two apart — <c>consent-text</c> is not a guid, so it never binds as an
/// <c>{id}</c>, and the two routes can be declared in either order.
/// </para>
/// <para>
/// The consent route carries <c>RequireIfMatch()</c> (428 <c>if_match_required</c>) and the <c>auth</c> rate-limit
/// policy, because it writes and is called by the SPA with a session; it carries no <c>Idempotency-Key</c> — §5.4's key
/// is for paid or platform-spending POSTs and this one neither spends nor calls out. The text route is a read and
/// carries neither.
/// </para>
/// <para>
/// The <c>{id}</c> is a <em>publication</em> id, so <see cref="EpisodeIdRouteParameter"/> is deliberately not
/// declared on it: the request-log enricher would otherwise label the request with a publication id as an episode id.
/// </para>
/// </remarks>
public static class PublicationEndpoints
{
    /// <summary>The group prefix: the publication resource of 06 README D6/D7.</summary>
    public const string GroupPrefix = "/api/v1/publications";

    /// <summary>The consent route, relative to <see cref="GroupPrefix"/>.</summary>
    public const string ConsentRoute = "/{id:guid}/consent";

    /// <summary>The consent-text route, relative to <see cref="GroupPrefix"/>.</summary>
    public const string ConsentTextRoute = "/consent-text";

    /// <summary>
    /// The feature flag that gates every YouTube publishing route (06 README D9); the same key the sibling publishing
    /// routes carry, declared once in <see cref="YouTubeEtaEndpoints"/>.
    /// </summary>
    public const string FeatureKey = YouTubeEtaEndpoints.FeatureKey;

    /// <summary>
    /// The <c>Cache-Control</c> of the consent text (10 CR-17): private to the caller's browser for an hour, so the
    /// dialogue opens instantly and a bump of <c>YouTube:ConsentTextVersion</c> is still seen within the hour by a
    /// cache revalidation — a shared proxy never keeps it, because the route is authenticated.
    /// </summary>
    public const string ConsentTextCacheControl = "private, max-age=3600";

    /// <summary>Maps the publication routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapPublicationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var publications = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy, and both routes are
        // called by the SPA with a bearer token, so their preflights need it too.
        publications.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        publications.MapPost(ConsentRoute, ConsentAsync)
            .RequireFeature(FeatureKey)
            .RequireAuthorization()
            .RequireIfMatch()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .Produces<PublicationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        publications.MapGet(ConsentTextRoute, GetConsentTextAsync)
            .RequireFeature(FeatureKey)
            .RequireAuthorization()
            .Produces<ConsentTextDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/publications/{id}/consent</c>: 200 with the consented publication and its post-commit
    /// <c>ETag</c> (<c>"pu-&lt;xmin&gt;"</c>), or the documented 400/401/404/409/412/428.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>ETag</c> is read after the unit of work committed (D15, 06 CR-2), through the same tenant-filtered
    /// <see cref="PostCommitETagReader"/> the create route uses, so the tag in the header names the committed row and
    /// is the one the client sends as the <c>If-Match</c> of its next cancel or PATCH.
    /// </para>
    /// <para>
    /// A 412 <c>publication_conflict</c> carries the row's <em>current</em> tag in the same header (09.1.4-T1's
    /// pattern): the handler compares <c>If-Match</c> after loading the row, so the current value is known, and the
    /// client can retry — or show the changed row — without a second read. No other failure sets a tag, because a
    /// 404 or a 409 must never hand a client something to cache (the episode-detail route's rule).
    /// </para>
    /// </remarks>
    private static async Task<IResult> ConsentAsync(
        Guid id,
        ConsentPublicationRequest request,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        // RequireIfMatchFilter has already answered 428 `if_match_required` for an absent or blank header
        // (src/NightTales.Api/Http/Filters/RequireIfMatchFilter.cs), so no empty value reaches ETags.Matches: the
        // binder's null here is the "filter is not on this route" case only. A present tag that no longer names the
        // row's current xmin is the 412 the handler answers with the row's current tag.
        var command = new ConsentPublicationCommand(id, request.ConsentTextVersion, ifMatch ?? string.Empty);

        var result = await sender.Send(command, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            if (string.Equals(result.FirstError.Code, ErrorCodes.PublicationConflict, StringComparison.Ordinal)
                && await etags.PublicationAsync(id, http.RequestAborted).ConfigureAwait(false) is { } current)
            {
                http.Response.Headers.ETag = current;
            }

            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        return await result
            .ToHttpResult(http, (publication, token) => etags.PublicationAsync(publication.Id, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/v1/publications/consent-text?locale=vi|en</c>: 200 with <c>{ version, text }</c> and a private
    /// hour of caching, or the documented 400 <c>validation_failed</c>/401/404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>locale</c> is bound as a string on purpose: an unknown value — or no value at all — has to be a coded 400
    /// under field <c>locale</c> rather than a silently defaulted language, because the text the owner consents to is
    /// evidence (D7) and must be the one the client asked to show. The two admitted wire values are <c>vi</c> and
    /// <c>en</c>, lower case and ordinal; the handler owns that check.
    /// </para>
    /// <para>
    /// <c>version</c> is the live <c>YouTube:ConsentTextVersion</c>, never a compiled-in constant, so a bump of the
    /// option changes what the client sends back to the consent route without a client release.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetConsentTextAsync(
        [FromQuery(Name = "locale")] string? locale,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender
            .Send(new GetConsentTextQuery(locale ?? string.Empty), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        http.Response.Headers.CacheControl = ConsentTextCacheControl;

        return result.ToHttpResult(http);
    }
}
