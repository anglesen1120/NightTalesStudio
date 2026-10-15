using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The render routes of an episode (09 README BD5, 09.6.1 plan §Design): <c>POST /episodes/{id}/render</c>
/// queues exactly one render job for one approved story.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is thin: bind, send one message through the D4 pipeline, answer 202 with
/// <c>Location: /render-jobs/{id}</c>. Another tenant's episode is a 404 from the tenant-filtered lookup,
/// never 403 (09 README D10).
/// </para>
/// <para>
/// A render reserves credit and spends platform money, so the route requires an <c>If-Match</c> (the story
/// the client read, 02 D9) <em>and</em> an <c>Idempotency-Key</c> (09 README D6), and carries the <c>paid</c>
/// rate-limit policy — exactly as <c>POST /episodes/{id}/fact-sheet/generate</c> and
/// <c>POST /episodes/{id}/story/generate</c> do. Authentication comes from the host's fallback policy (09.3
/// D4); <c>RequireAuthorization</c> is stated anyway so the intent survives a future fallback change.
/// </para>
/// </remarks>
public static class RenderingEndpoints
{
    /// <summary>The render route: one queued render job for one approved episode.</summary>
    public const string RenderRoute = EpisodeEndpoints.GroupPrefix + "/{id:guid}/render";

    /// <summary>Maps the render route onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapRenderingEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        EpisodeIdRouteParameter.Declare(
            routes.MapPost(RenderRoute, RenderAsync)
                .RequireIfMatch()
                .RequireIdempotencyKey()
                .RequireAuthorization()
                .RequireRateLimiting(RateLimitPolicies.Paid)
                .Produces<RenderJobAcceptedDto>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .ProducesProblem(StatusCodes.Status429TooManyRequests)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/render</c>: 202 with the queued job and
    /// <c>Location: /render-jobs/{id}</c>, or the documented
    /// 400/401/404/409/412/422/428/429/503.
    /// </summary>
    /// <param name="id">The episode, from the route.</param>
    /// <param name="ifMatch">The required <c>If-Match</c> header value; the filter answered 428 when absent.</param>
    /// <param name="idempotencyKey">The <c>Idempotency-Key</c> header value; the filter answered 400 when absent.</param>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="http">The request, for the <c>Location</c> header.</param>
    /// <param name="ct">The request cancellation token.</param>
    private static async Task<IResult> RenderAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        [FromHeader(Name = RequireIdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
            .Send(new RenderEpisodeCommand(id, ifMatch, idempotencyKey ?? string.Empty), ct)
            .ConfigureAwait(false))
            .ToAcceptedResult(http, accepted => $"/render-jobs/{accepted.RenderJobId}");
}
