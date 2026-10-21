using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research;
using NightTales.Application.Features.Research.Commands.ApproveFactSheet;
using NightTales.Application.Features.Research.Commands.GenerateFactSheet;
using NightTales.Application.Features.Research.Commands.UpdateFactSheet;
using NightTales.Application.Features.Research.Queries.GetFactSheet;

namespace NightTales.Api.Endpoints.Research;

/// <summary>
/// The fact-sheet routes of an episode (02.5.3 plan §5, 02.5.4-T1 plan §4): generate (02.5.3-T2), and the read,
/// edit and approve routes of this task with their <c>fs-</c> ETag.
/// </summary>
/// <remarks>
/// The endpoints are thin: bind, send one message through the D4 pipeline, answer. Another tenant's episode is a
/// 404 from the tenant-filtered lookup, never 403. Generation reserves credit and spends platform money, so the
/// route carries the <c>paid</c> rate-limit policy and requires an <c>Idempotency-Key</c> (09 README D6), exactly
/// as <c>POST /episodes/{id}/story/generate</c> does; the read, edit and approve routes are free and carry no
/// policy, exactly as the story read/edit/approve routes do today (an accepted residual, not reinvented here).
/// The edit and approve routes take <c>If-Match</c> (<c>RequireIfMatch</c> — the 428 before the handler) and no
/// <c>Idempotency-Key</c>: the ETag is the concurrency token, so a replay is a 412, never a second write.
/// </remarks>
public static class FactSheetEndpoints
{
    /// <summary>The generation route.</summary>
    public const string GenerateRoute = "/api/v1/episodes/{id:guid}/fact-sheet/generate";

    /// <summary>The read route (<c>GET</c>) and the edit route (<c>PUT</c>) of the episode's fact sheet.</summary>
    public const string GetRoute = "/api/v1/episodes/{id:guid}/fact-sheet";

    /// <summary>The edit route, the same address as the read with <c>PUT</c>.</summary>
    public const string PutRoute = GetRoute;

    /// <summary>The approve route.</summary>
    public const string ApproveRoute = GetRoute + "/approve";

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

        EpisodeIdRouteParameter.Declare(
            routes.MapGet(GetRoute, GetAsync)
                .Produces<FactSheetDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        EpisodeIdRouteParameter.Declare(
            routes.MapPut(PutRoute, UpdateAsync)
                .RequireIfMatch()
                .Produces<FactSheetSummaryDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        EpisodeIdRouteParameter.Declare(
            routes.MapPost(ApproveRoute, ApproveAsync)
                .RequireIfMatch()
                .Produces<FactSheetSummaryDto>(StatusCodes.Status200OK)
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

    /// <summary>
    /// <c>GET /api/v1/episodes/{id}/fact-sheet</c>: 200 with the current version, its facts with their resolved
    /// sources, and <c>ETag: "fs-&lt;id&gt;"</c> for the next <c>If-Match</c>, or the documented 401/404.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetFactSheetQuery(id), ct).ConfigureAwait(false);
        if (!result.IsError)
        {
            http.Response.Headers.Vary = "Authorization";
            http.Response.Headers.CacheControl = "no-cache";
        }

        return result.ToHttpResult(http);
    }

    /// <summary>
    /// <c>PUT /api/v1/episodes/{id}/fact-sheet</c>: 200 with the saved draft version and its <c>fs-</c> ETag, or the
    /// documented 400 <c>fact_sheet_invalid_content</c> / 404 <c>fact_sheet_not_found</c> / 409
    /// <c>episode_invalid_state</c> / 412 <c>fact_sheet_conflict</c> / 428 <c>if_match_required</c>.
    /// </summary>
    /// <remarks>
    /// The ETag is emitted from the DTO itself and not re-read after commit: it names the new version's row id
    /// (<c>"fs-&lt;id&gt;"</c>), not an <c>xmin</c>, so no later commit can change it (02 README D9). An empty body
    /// is answered here, in the same 400 shape as a body that fails the fact rules.
    /// </remarks>
    private static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] FactSheetContentDto? content,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        if (content is null)
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Validation(
                    ErrorCodes.FactSheetInvalidContent,
                    "The body is not a fact-sheet document.",
                    new Dictionary<string, object> { [ErrorMetadata.Field] = "body" })],
                http);
        }

        return (await sender
            .Send(new UpdateFactSheetCommand(id, ifMatch ?? string.Empty, content), ct)
            .ConfigureAwait(false))
            .ToHttpResult(http);
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/fact-sheet/approve</c>: 200 with the approved version and the <c>fs-</c> ETag
    /// that names it, or the documented 400 / 404 <c>fact_sheet_not_found</c> / 409 <c>episode_invalid_state</c>,
    /// <c>fact_sheet_sources_insufficient</c> / 412 <c>fact_sheet_conflict</c> / 428 <c>if_match_required</c>.
    /// </summary>
    private static async Task<IResult> ApproveAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
            .Send(new ApproveFactSheetCommand(id, ifMatch ?? string.Empty), ct)
            .ConfigureAwait(false))
            .ToHttpResult(http);
}
