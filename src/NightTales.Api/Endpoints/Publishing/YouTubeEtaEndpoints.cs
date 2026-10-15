using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Features.Publishing.Queries.GetYouTubeEta;

namespace NightTales.Api.Endpoints.Publishing;

/// <summary>
/// <c>GET /api/v1/publishing/youtube/eta</c> (06.3.1-T1, 06 README D9): the platform's remaining YouTube
/// upload units for the current Pacific quota day, and when the next slot opens once the day is spent.
/// </summary>
/// <remarks>
/// <para>
/// The counter is platform-wide, so the answer is the same for every authenticated caller. The route is
/// deliberately thin: it sends <see cref="GetYouTubeEtaQuery"/> through Mediator and maps the
/// <c>ErrorOr</c> result onto HTTP.
/// </para>
/// <para>
/// <c>RequireAuthorization()</c> sits on the route, so an anonymous caller answers 401 before the
/// <c>youtube_publish</c> feature filter runs. An authenticated caller with the flag off answers 404
/// <c>feature_disabled</c>. No idempotency key and no <c>If-Match</c>: the route changes no state.
/// </para>
/// </remarks>
public static class YouTubeEtaEndpoints
{
    /// <summary>The route, under the publishing prefix.</summary>
    public const string Route = "/api/v1/publishing/youtube/eta";

    /// <summary>The feature flag that gates every YouTube publishing route (06 README D9).</summary>
    public const string FeatureKey = "youtube_publish";

    /// <summary>Maps the route onto the host's endpoint route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapYouTubeEtaEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Route, GetAsync)
            .RequireFeature(FeatureKey)
            .RequireAuthorization()
            .Produces<YouTubeEtaDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/v1/publishing/youtube/eta</c>: 200 with <c>{ available, nextSlotAt }</c>, or the mapped
    /// error problem when the query fails.
    /// </summary>
    private static async Task<IResult> GetAsync(ISender sender, HttpContext http, CancellationToken ct)
        => (await sender.Send(new GetYouTubeEtaQuery(), ct).ConfigureAwait(false)).ToHttpResult(http);
}
