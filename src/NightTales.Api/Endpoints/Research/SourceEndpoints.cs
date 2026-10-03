using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Application.Features.Research.Sources.Commands.RemoveSource;
using NightTales.Application.Features.Research.Sources.Commands.RetrySource;
using NightTales.Application.Features.Research.Sources.Queries.ListSources;

namespace NightTales.Api.Endpoints.Research;

/// <summary>
/// The story-source routes of an episode (02.5.1 plan §5.1): add a URL or a note, list with the limit block, remove,
/// retry a failed fetch.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are thin: bind, send one message through the D4 pipeline, answer. Every route is authorised by
/// default (an anonymous caller is 401 before a handler runs); another tenant's episode is a 404 from the tenant-filtered
/// lookup, never 403. The <c>429</c>'s <c>Retry-After</c> header is written by the shared ProblemDetails mapper from the
/// error's <c>retryAfterSeconds</c> metadata (02 CR-20, [DECISION T43]); nothing here sets it.
/// </para>
/// <para>
/// <c>Idempotency-Key</c> is optional on <c>POST</c>: adding a source spends no credits, so no route filter requires it
/// (09 README D6), but a key that is sent is honoured — a replay returns the first response and creates no second row.
/// </para>
/// </remarks>
public static class SourceEndpoints
{
    /// <summary>The group's prefix.</summary>
    public const string GroupPrefix = "/api/v1/episodes/{episodeId:guid}/sources";

    /// <summary>Maps the source routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var sources = routes.MapGroup(GroupPrefix);
        sources.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        EpisodeIdRouteParameter.Declare(
            sources.MapGet(string.Empty, ListAsync)
                .Produces<SourceListDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound),
            "episodeId");

        EpisodeIdRouteParameter.Declare(
            sources.MapPost(string.Empty, AddAsync)
                .Produces<SourceDto>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status429TooManyRequests),
            "episodeId");

        EpisodeIdRouteParameter.Declare(
            sources.MapDelete("/{sourceId:guid}", RemoveAsync)
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict),
            "episodeId");

        EpisodeIdRouteParameter.Declare(
            sources.MapPost("/{sourceId:guid}/retry", RetryAsync)
                .Produces<SourceDto>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status429TooManyRequests),
            "episodeId");

        return routes;
    }

    /// <summary><c>GET</c>: 200 with the sources and the limit block, or 404.</summary>
    private static async Task<IResult> ListAsync(Guid episodeId, ISender sender, HttpContext http, CancellationToken ct)
        => (await sender.Send(new ListSourcesQuery(episodeId), ct).ConfigureAwait(false)).ToHttpResult(http);

    /// <summary>
    /// <c>POST</c>: 201 with the source, or 400 <c>source_url_invalid</c> / <c>source_domain_not_allowed</c> /
    /// <c>validation_failed</c>, 404, 409 <c>source_duplicate</c> / <c>source_limit_reached</c> /
    /// <c>episode_invalid_state</c>, 429 <c>source_fetch_limit_reached</c>.
    /// </summary>
    private static async Task<IResult> AddAsync(
        Guid episodeId,
        AddSourceRequest request,
        [FromHeader(Name = RequireIdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        if (idempotencyKey is not null && !RequireIdempotencyKeyFilter.IsValidKey(idempotencyKey))
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Failure(ErrorCodes.IdempotencyKeyRequired, RequireIdempotencyKeyFilter.RejectionDescription)],
                http);
        }

        var result = await sender
            .Send(new AddSourceCommand(episodeId, request.Url, request.NoteText, idempotencyKey), ct)
            .ConfigureAwait(false);

        return await result
            .ToCreatedResult(
                http,
                created => $"/api/v1/episodes/{episodeId}/sources/{created.Id}",
                (_, _) => Task.FromResult<string?>(null))
            .ConfigureAwait(false);
    }

    /// <summary><c>DELETE</c>: 204, or 404 / 409 <c>episode_invalid_state</c>.</summary>
    private static async Task<IResult> RemoveAsync(
        Guid episodeId,
        Guid sourceId,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new RemoveSourceCommand(episodeId, sourceId), ct).ConfigureAwait(false))
            .ToNoContentResult(http);

    /// <summary><c>POST …/retry</c>: 202 with the pending source, or 404 / 409 / 429.</summary>
    private static async Task<IResult> RetryAsync(
        Guid episodeId,
        Guid sourceId,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new RetrySourceCommand(episodeId, sourceId), ct).ConfigureAwait(false))
            .ToAcceptedResult(http);
}

/// <summary>
/// The <c>POST /api/v1/episodes/{episodeId}/sources</c> body (02.5.1 plan §5.1): exactly one of the members is set.
/// </summary>
/// <param name="Url">The URL to fetch, as pasted.</param>
/// <param name="NoteText">The user's own note.</param>
public sealed record AddSourceRequest(string? Url, string? NoteText);
