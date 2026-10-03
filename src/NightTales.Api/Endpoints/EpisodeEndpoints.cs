using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common.Errors;
using NightTales.Application.Common.Paging;
using NightTales.Application.Features.Episodes.Commands.CreateEpisode;
using NightTales.Application.Features.Episodes.Commands.DeleteEpisode;
using NightTales.Application.Features.Episodes.Queries.GetEpisode;
using NightTales.Application.Features.Episodes.Queries.ListEpisodes;
using NightTales.Domain.Episodes;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The episode routes (09 README D14, 09 §4 09.5.1, plan §5): create under a series, list with cursor
/// pages, read one, cancel one.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are deliberately thin (09.1.2-T1): they bind the frozen body and query string, send one
/// message through the D4 pipeline and turn the outcome into the documented response. The post-commit
/// ETag read (<see cref="PostCommitETagReader"/>, 09 README D15) is theirs because only the Api knows
/// what the client sent and what the headers are — the read itself lives outside
/// <c>Api/Endpoints</c>, where the architecture rule <c>Endpoints_DoNotReferenceDbContexts</c> requires
/// it.
/// </para>
/// <para>
/// No route carries <c>AllowAnonymous</c>: every message is denied by default, so an anonymous caller
/// answers 401 <c>unauthorized</c> before a handler runs (09 README D4/D10). A caller who is
/// authenticated but does not own the episode or the series gets 404 from the tenant-filtered lookup,
/// never 403.
/// </para>
/// <para>
/// No route carries <c>RequireIdempotencyKey()</c>: creating an episode spends no credits and calls no
/// platform ([DECISION] 09.5.1 plan §5/§Notes, 00 §8). <c>DELETE</c> has no <c>RequireIfMatch()</c>
/// either — <c>If-Match</c> is optional on it ([DECISION] plan §Notes).
/// </para>
/// </remarks>
public static class EpisodeEndpoints
{
    /// <summary>The group's prefix; the list, read and cancel routes hang under it.</summary>
    public const string GroupPrefix = "/api/v1/episodes";

    /// <summary>The create route, which belongs to the series resource (09.5.1 plan §5).</summary>
    public const string CreateRoute = "/api/v1/series/{seriesId:guid}/episodes";

    /// <summary>Maps the episode routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapEpisodeEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(CreateRoute, CreateAsync)
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy.
            .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        var episodes = routes.MapGroup(GroupPrefix);
        episodes.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        episodes.MapGet(string.Empty, ListAsync)
            .Produces<Page<EpisodeListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        EpisodeIdRouteParameter.Declare(
            episodes.MapGet("/{id:guid}", GetAsync)
                .Produces<EpisodeDetailDto>(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status304NotModified)
                .ProducesProblem(StatusCodes.Status404NotFound));

        EpisodeIdRouteParameter.Declare(
            episodes.MapDelete("/{id:guid}", DeleteAsync)
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed));

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/series/{seriesId}/episodes</c>: 201 with <c>Location</c> and the committed
    /// <c>ETag</c>, or the documented 400/404/409.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid seriesId,
        CreateEpisodeRequest request,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var command = new CreateEpisodeCommand(
            seriesId,
            request.Title,
            request.Brief,
            request.LengthTier);

        var result = await sender.Send(command, ct).ConfigureAwait(false);

        return await result
            .ToCreatedResult(
                http,
                created => $"{GroupPrefix}/{created}",
                (created, token) => etags.EpisodeAsync(created, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/v1/episodes?seriesId=&amp;status=&amp;cursor=&amp;limit=</c>: 200 with
    /// <c>{ items, nextCursor }</c>, or 400 <c>validation_failed</c> for an unknown status, a bad cursor
    /// or a limit out of 1–100.
    /// </summary>
    /// <remarks>
    /// The four values are bound one by one and not as one [AsParameters] record: the absent case of each
    /// has a documented default that the endpoint — not the binder — applies. <c>status</c> is bound as a
    /// string on purpose: the wire value is the snake_case name (09 README D14), which the built-in enum
    /// binder would refuse, and an unknown name has to be a 400 under field <c>status</c> rather than a
    /// silently dropped filter.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        [FromQuery(Name = "seriesId")] Guid? seriesId,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] int? limit,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        EpisodeStatus? statusFilter = null;

        if (status is not null)
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return ProblemDetailsWriter.ToProblem(
                    [Errors.ValidationFailed("status", "The status filter is not a known episode status.")],
                    http);
            }

            statusFilter = parsedStatus;
        }

        var query = new ListEpisodesQuery(
            seriesId,
            statusFilter,
            cursor,
            limit ?? ListEpisodesQuery.DefaultLimit);

        return (await sender.Send(query, ct).ConfigureAwait(false)).ToHttpResult(http);
    }

    /// <summary>
    /// <c>GET /api/v1/episodes/{id}</c>: 200 with the episode page and its current <c>ETag</c>, 304 when
    /// the client already holds that tag, or 404 for a missing, foreign or cancelled one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The conditional answer belongs to the endpoint, not to
    /// <see cref="ResultExtensions.ToHttpResult{T}(ErrorOr{T}, HttpContext)"/>: <c>xmin</c> is knowable
    /// only from the handler's own projection, so the handler always builds the whole DTO and the
    /// endpoint decides what to send (09.5.3 plan §5 [DECISION]). The "read <c>xmin</c> alone, then the
    /// blocks only on a miss" optimisation is deliberately not taken — one handler, one code path.
    /// </para>
    /// <para>
    /// <c>If-None-Match</c> is compared as the exact strong tag (09 README D7/D15): a weak
    /// (<c>W/"ep-5"</c>), comma-separated or multi-valued header is a miss and answers 200, which is
    /// already what the header's own string form means — a multi-valued header joins its values, so it
    /// can never equal the one strong tag.
    /// </para>
    /// <para>
    /// The 200 and the 304 both carry <c>Cache-Control: no-cache</c> and <c>Vary: Authorization</c>: the
    /// resource is per-tenant (D10) and every answer must be revalidated, so a 200 a proxy cached
    /// heuristically can never hide a later change — the new <c>xmin</c> differs and the revalidation is
    /// a miss.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetEpisodeByIdQuery(id), ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return result.ToHttpResult(http);
        }

        var dto = result.Value;

        // D15: the same value in the header and in the DTO field, set before the conditional branch so a
        // 304 carries it too — the helper only sets it on its way to the 200 body.
        http.Response.Headers.ETag = dto.Etag;
        http.Response.Headers.Vary = "Authorization";
        http.Response.Headers.CacheControl = "no-cache";

        if (string.Equals(http.Request.Headers.IfNoneMatch.ToString(), dto.Etag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return result.ToHttpResult(http);
    }

    /// <summary>
    /// <c>DELETE /api/v1/episodes/{id}</c>: 204, cancelling the episode and scheduling its assets for
    /// retention, or the documented 404/409/412.
    /// </summary>
    /// <remarks>
    /// The header is optional, so there is no route filter and no 428: a stale tag is answered 412 by the
    /// handler, which is the only place that has read the row's <c>xmin</c>. The binder yields
    /// <see langword="null"/> only for an absent header, so any present value — an empty one included — is
    /// compared and answers 412 <c>episode_conflict</c> (CR-1).
    /// </remarks>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new DeleteEpisodeCommand(id, ifMatch), ct).ConfigureAwait(false))
            .ToNoContentResult(http);

    /// <summary>
    /// Parses the <c>?status=</c> filter in the snake_case wire form (09 README D14:
    /// <c>story_generating</c>, not <c>StoryGenerating</c>), which the built-in enum binder does not
    /// accept. The comparison is ordinal: the filter is a machine value, not user input.
    /// </summary>
    private static bool TryParseStatus(string value, out EpisodeStatus status)
    {
        foreach (var candidate in Enum.GetValues<EpisodeStatus>())
        {
            if (string.Equals(
                JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.ToString()),
                value,
                StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }
}

/// <summary>
/// The <c>POST /api/v1/series/{seriesId}/episodes</c> body (09.5.1 plan §5): the members a client may
/// send.
/// </summary>
/// <remarks>
/// <c>seriesId</c> is the route value and <c>userId</c> is the owner of that series, so neither is a body
/// member: the handler copies both from the loaded series, and a client cannot create an episode for
/// another tenant or another series than the one the route names (09.5.1 plan §8).
/// </remarks>
/// <param name="Title">The optional title, ≤ 120 characters after trimming; empty becomes <see langword="null"/>.</param>
/// <param name="Brief">The brief, 1–1,000 characters after trimming.</param>
/// <param name="LengthTier">The story length tier, snake_case (<c>short</c>, <c>standard</c>, <c>long</c>).</param>
public sealed record CreateEpisodeRequest(
    string? Title,
    string Brief,
    LengthTier LengthTier);
