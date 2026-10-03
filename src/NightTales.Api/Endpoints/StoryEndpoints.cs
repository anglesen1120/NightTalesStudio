using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The story routes (02 README D9, 02.2.2 plan §5.1). Only generation is mapped here so far: the other story routes
/// belong to their own tasks.
/// </summary>
/// <remarks>
/// Like every route it is denied to anonymous callers by the pipeline; a caller who does not own the episode gets
/// 404 from the tenant-filtered lookup, never 403. The route needs no <c>If-Match</c> (D9) but requires an
/// <c>Idempotency-Key</c>, because it reserves credit and spends platform money (09 README D6).
/// </remarks>
public static class StoryEndpoints
{
    /// <summary>The generate route.</summary>
    public const string GenerateRoute = "/api/v1/episodes/{id:guid}/story/generate";

    /// <summary>Maps the story routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapStoryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        EpisodeIdRouteParameter.Declare(
            routes.MapPost(GenerateRoute, GenerateAsync)
                .RequireIdempotencyKey()
                .Produces<GenerateStoryAccepted>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status429TooManyRequests)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/story/generate</c>: 202 with <c>Location: /api/v1/episodes/{id}</c> and the queued
    /// run, or the documented 400/404/409/422/429.
    /// </summary>
    private static async Task<IResult> GenerateAsync(
        Guid id,
        [FromHeader(Name = RequireIdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
            .Send(new GenerateStoryCommand(id, GenerationTrigger.User, idempotencyKey), ct)
            .ConfigureAwait(false))
            .ToAcceptedResult(http, _ => $"{EpisodeEndpoints.GroupPrefix}/{id}");
}
