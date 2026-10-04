using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Commands.EditStory;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The story routes (02 README D9, 02.2.2 plan §5.1, 02.3.1 plan §Route). Generation and the full edit are mapped here
/// so far: the other story routes belong to their own tasks.
/// </summary>
/// <remarks>
/// Like every route it is denied to anonymous callers by the pipeline; a caller who does not own the episode gets
/// 404 from the tenant-filtered lookup, never 403. Generation needs no <c>If-Match</c> (D9) but requires an
/// <c>Idempotency-Key</c>, because it reserves credit and spends platform money (09 README D6). The edit is the
/// opposite: it spends nothing, so it needs no key, and it requires the story <c>If-Match</c>.
/// </remarks>
public static class StoryEndpoints
{
    /// <summary>The generate route.</summary>
    public const string GenerateRoute = "/api/v1/episodes/{id:guid}/story/generate";

    /// <summary>The story route: <c>PUT</c> saves an edit (02.3.1-T1).</summary>
    public const string StoryRoute = "/api/v1/episodes/{id:guid}/story";

    /// <summary>Maps the story routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapStoryEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        EpisodeIdRouteParameter.Declare(
            routes.MapPost(GenerateRoute, GenerateAsync)
                .RequireIdempotencyKey()
                .RequireRateLimiting(RateLimitPolicies.Paid)
                .Produces<GenerateStoryAccepted>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status429TooManyRequests)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        EpisodeIdRouteParameter.Declare(
            routes.MapPut(StoryRoute, EditAsync)
                .RequireIfMatch()
                .Accepts<JsonElement>("application/json")
                .Produces<StoryVersionDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        return routes;
    }

    /// <summary>
    /// <c>PUT /api/v1/episodes/{id}/story</c> (02.3.1-T1): 200 with the saved version and its <c>ETag</c> (the unchanged
    /// one for an identical story), or the documented 400 <c>story_invalid_content</c> / 404 / 409
    /// <c>episode_invalid_state</c> / 412 <c>story_version_conflict</c> / 428 <c>if_match_required</c>.
    /// </summary>
    /// <remarks>
    /// The body is read as <c>story.v1</c> with the stored document's snake_case policy; members it does not know
    /// (<c>schema_version</c>, <c>fact_sources</c>) are skipped, so the backend never trusts a client-sent source or URL.
    /// </remarks>
    private static async Task<IResult> EditAsync(
        Guid id,
        [FromBody] JsonElement body,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        if (!StoryContentJson.TryRead(body, out var content))
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Validation(
                    ErrorCodes.StoryInvalidContent,
                    "The body is not a story.v1 document.",
                    new Dictionary<string, object> { [ErrorMetadata.Field] = "body" })],
                http);
        }

        return (await sender
            .Send(new EditStoryCommand(id, content, ifMatch ?? string.Empty), ct)
            .ConfigureAwait(false))
            .ToHttpResult(http);
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/story/generate</c>: 202 with <c>Location: /api/v1/episodes/{id}</c> and the queued
    /// run, or the documented 400/401/403/404/409/422/429. The route carries the <c>paid</c> rate-limit policy
    /// (02.2.2 plan §8/§5.1): the 31st request in a minute is a 429 <c>rate_limited</c>.
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
