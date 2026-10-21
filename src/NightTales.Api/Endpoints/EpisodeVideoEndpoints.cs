using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Contracts.Video;
using NightTales.Api.Http;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The episode video route (09 README BD10, 09.7.2 plan §5): one presigned read URL per request.
/// </summary>
/// <remarks>
/// <para>
/// The route hangs under the episode group's prefix and is deliberately thin: it binds the id, calls
/// <see cref="EpisodeVideoUrlIssuer"/> — which lives in <c>Api/Http</c> because the architecture rule
/// <c>Endpoints_DoNotReferenceDbContexts</c> forbids an endpoint from naming <c>IReadDb</c> — and turns
/// the outcome into the documented response.
/// </para>
/// <para>
/// Unlike the other episode routes there is no Mediator message behind it, so the deny-by-default
/// behaviour of the D4 pipeline is not in play: the route carries
/// <c>RequireAuthorization()</c> itself, and an anonymous caller answers 401 <c>unauthorized</c>
/// instead of reaching the tenant-filtered read (which would answer a misleading 404).
/// </para>
/// <para>
/// No <c>RequireIfMatch()</c> and no idempotency key: a <c>GET</c> changes no episode state, and the
/// only row it writes is the audit entry.
/// </para>
/// </remarks>
public static class EpisodeVideoEndpoints
{
    /// <summary>The route, under the episode group's frozen prefix (09 README D14).</summary>
    public const string Route = $"{EpisodeEndpoints.GroupPrefix}/{{id:guid}}/video-url";

    /// <summary>Maps the route onto the host's endpoint route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapEpisodeVideoEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        EpisodeIdRouteParameter.Declare(
            routes.MapGet(Route, GetAsync)
                .RequireAuthorization()
                .Produces<VideoUrlDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable));

        return routes;
    }

    /// <summary>
    /// <c>GET /api/v1/episodes/{id}/video-url</c>: 200 with <c>{ url, expiresAt }</c>, 404 for an
    /// unknown, foreign or not-yet-rendered episode, or 503 when the store or the audit write fails.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id,
        EpisodeVideoUrlIssuer issuer,
        HttpContext http,
        CancellationToken cancellationToken)
        => (await issuer.IssueAsync(id, cancellationToken).ConfigureAwait(false)).ToHttpResult(http);
}
