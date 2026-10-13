using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Features.Research.GenerateFactSheet;

namespace NightTales.Api.Endpoints.Research;

/// <summary>
/// The fact-sheet routes of an episode (02.5.3 plan §5). This task adds the generation request; the read,
/// edit and approve routes belong to 02.5.4-T1.
/// </summary>
/// <remarks>
/// The endpoints are thin: bind, send one message through the D4 pipeline, answer. Another tenant's episode is a
/// 404 from the tenant-filtered lookup, never 403. Generation reserves credit and spends platform money, so the
/// route carries the <c>paid</c> rate-limit policy and requires an <c>Idempotency-Key</c> (09 README D6), exactly
/// as <c>POST /episodes/{id}/story/generate</c> does.
/// </remarks>
public static class FactSheetEndpoints
{
    /// <summary>The generation route.</summary>
    public const string GenerateRoute = "/api/v1/episodes/{id:guid}/fact-sheet/generate";

    /// <summary>Maps the fact-sheet routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapFactSheetEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        EpisodeIdRouteParameter.Declare(
            routes.MapPost(GenerateRoute, GenerateAsync)
                .RequireIdempotencyKey()
                .RequireRateLimiting(RateLimitPolicies.Paid)
                .Produces<FactSheetRunDto>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status429TooManyRequests)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/fact-sheet/generate</c>: 202 with <c>Location: /api/v1/episodes/{id}</c> and
    /// the queued run, or the documented 400/401/404/409/429.
    /// </summary>
    private static async Task<IResult> GenerateAsync(
        Guid id,
        [FromHeader(Name = RequireIdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
            .Send(new GenerateFactSheetCommand(id, idempotencyKey ?? string.Empty), ct)
            .ConfigureAwait(false))
            .ToAcceptedResult(http, _ => $"{EpisodeEndpoints.GroupPrefix}/{id}");
}
