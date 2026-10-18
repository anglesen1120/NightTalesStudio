using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering.Commands.CancelRender;
using NightTales.Application.Features.Rendering.Commands.RenderEpisode;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The render routes (09 README BD5/BD8, 09.6.1/09.6.4 plan §Design):
/// <c>POST /episodes/{id}/render</c> queues exactly one render job for one approved story, and
/// <c>DELETE /render-jobs/{id}</c> stops one of them.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are thin: bind, send one message through the D4 pipeline, answer 202 with
/// <c>Location: /render-jobs/{id}</c>, or 204 for a cancel. Another tenant's episode or job is a 404 from the
/// tenant-filtered lookup, never 403 (09 README D10).
/// </para>
/// <para>
/// A render reserves credit and spends platform money, so its POST requires an <c>If-Match</c> (the story
/// the client read, 02 D9) <em>and</em> an <c>Idempotency-Key</c> (09 README D6), and carries the <c>paid</c>
/// rate-limit policy — exactly as <c>POST /episodes/{id}/fact-sheet/generate</c> and
/// <c>POST /episodes/{id}/story/generate</c> do. The cancel is the other way round: it frees money and is
/// idempotent by the row's own status, so its DELETE requires neither header and carries no rate policy.
/// Authentication comes from the host's fallback policy (09.3 D4); <c>RequireAuthorization</c> is stated
/// anyway so the intent survives a future fallback change.
/// </para>
/// </remarks>
public static class RenderingEndpoints
{
    /// <summary>The render route: one queued render job for one approved episode.</summary>
    public const string RenderRoute = EpisodeEndpoints.GroupPrefix + "/{id:guid}/render";

    /// <summary>
    /// The cancel route of a render job (09 README BD8, 09.6.4-T1): the job is addressed by its own id, not
    /// through its episode, so the versioned base is spelled out here as the top-level catalog routes spell
    /// theirs. The <c>Location</c> the render route answers with is the relative <c>/render-jobs/{id}</c>.
    /// </summary>
    public const string RenderJobRoute = "/api/v1/render-jobs/{id:guid}";

    /// <summary>Maps the render routes onto the host's route builder.</summary>
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

        // No EpisodeIdRouteParameter here: the route's {id} is a render job, and the request-log enricher
        // (09.9.3-T1) must not label it as an episode.
        routes.MapDelete(RenderJobRoute, CancelAsync)
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

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

    /// <summary>
    /// <c>DELETE /api/v1/render-jobs/{id}</c>: 204 when the job was cancelled, or when the cancel was requested
    /// or was already applied, and the documented 401/404/409.
    /// </summary>
    /// <remarks>
    /// The route carries neither <c>If-Match</c> nor <c>Idempotency-Key</c> ([DECISION] 09.6.4 plan §Notes): the
    /// cancel frees money instead of spending it, and it is idempotent by the row's own status, so a repeat is the
    /// same 204. It carries no <c>paid</c> policy either — the D-policy of BD14 limits paid <em>POSTs</em>. No
    /// node is reachable from here: the adapter's cancel call is the Worker's outbox handler's job, which is why
    /// this endpoint resolves no render client at all (BD11).
    /// </remarks>
    /// <param name="id">The render job, from the route; another user's job is a 404 (09 README D10).</param>
    /// <param name="sender">The Mediator sender.</param>
    /// <param name="http">The request, for the problem details of an error result.</param>
    /// <param name="ct">The request cancellation token.</param>
    private static async Task<IResult> CancelAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new CancelRenderCommand(id), ct).ConfigureAwait(false))
            .ToNoContentResult(http);
}
