using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common.Errors;
using NightTales.Application.Common.Paging;
using NightTales.Application.Features.Series.Commands.ArchiveSeries;
using NightTales.Application.Features.Series.Commands.CreateSeries;
using NightTales.Application.Features.Series.Commands.UpdateSeries;
using NightTales.Application.Features.Series.Queries.GetSeries;
using NightTales.Application.Features.Series.Queries.ListSeries;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The <c>/api/v1/series</c> route group (09 README D14, 09 §4 09.4.1, plan §5): create, list, read,
/// patch and archive one's own series.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are deliberately thin (09.1.2-T1): they bind the frozen bodies, send one message
/// through the D4 pipeline and turn the outcome into the documented response. Two things are theirs
/// alone — the wire model that tells an absent patch member from an explicit <c>null</c>
/// (<see cref="UpdateSeriesRequest"/>), and the post-commit ETag read (<see cref="PostCommitETagReader"/>,
/// 09 README D15) — because only the Api knows what the client sent and what the headers are.
/// </para>
/// <para>
/// No route carries <c>AllowAnonymous</c>: every message is denied by default, so an anonymous caller
/// answers 401 <c>unauthorized</c> before a handler runs — exactly like <c>POST /auth/logout</c>
/// (09 README D4/D10). A caller who is authenticated but does not own the series gets 404 from the
/// tenant-filtered lookup, never 403.
/// </para>
/// <para>
/// No route carries <c>RequireIdempotencyKey()</c>: 09.4.1 plan §4 states there is no idempotency on
/// this resource, because none of its effects is paid. The rules (kind, bounds) have a unit test of
/// their own, so this note is about this resource and not about the filter.
/// </para>
/// </remarks>
public static class SeriesEndpoints
{
    /// <summary>The group's prefix; every series route of 09.4 hangs under it.</summary>
    public const string GroupPrefix = "/api/v1/series";

    /// <summary>Maps the series routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapSeriesEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var series = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy; the
        // credentialed one belongs to the auth routes alone.
        series.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        series.MapPost(string.Empty, CreateAsync)
            .Produces<SeriesCreated>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        series.MapGet(string.Empty, ListAsync)
            .Produces<Page<SeriesListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        series.MapGet("/{id:guid}", GetAsync)
            .Produces<SeriesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        series.MapPatch("/{id:guid}", PatchAsync)
            .RequireIfMatch()
            .Produces<SeriesUpdateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        series.MapDelete("/{id:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/series</c>: 201 with <c>Location</c> and the committed <c>ETag</c>, or the
    /// documented 400.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateSeriesCommand command,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct).ConfigureAwait(false);

        return await result
            .ToCreatedResult(
                http,
                created => $"{GroupPrefix}/{created.Id}",
                (created, token) => etags.SeriesAsync(created.Id, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/v1/series?cursor=&amp;limit=&amp;includeArchived=</c>: 200 with
    /// <c>{ items, nextCursor }</c>, or 400 <c>validation_failed</c> for a bad cursor or limit.
    /// </summary>
    /// <remarks>
    /// The three values are bound one by one and not as one [AsParameters] record: the absent case of
    /// each has a documented default (20, false) that the endpoint — not the binder — applies.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] int? limit,
        [FromQuery(Name = "includeArchived")] bool? includeArchived,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var query = new ListSeriesQuery(
            cursor,
            limit ?? ListSeriesQuery.DefaultLimit,
            includeArchived ?? false);

        return (await sender.Send(query, ct).ConfigureAwait(false)).ToHttpResult(http);
    }

    /// <summary>
    /// <c>GET /api/v1/series/{id}</c>: 200 with the series and its current <c>ETag</c> — archived
    /// series included — or 404 for a missing or foreign one.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new GetSeriesQuery(id), ct).ConfigureAwait(false)).ToHttpResult(http);

    /// <summary>
    /// <c>PATCH /api/v1/series/{id}</c>: 200 with the new <c>etag</c> and the <c>ETag</c> header, or the
    /// documented 400/404/409/412/428.
    /// </summary>
    /// <remarks>
    /// The <c>If-Match</c> filter answered 428 before this method ran, so <paramref name="ifMatch"/> is
    /// present; the empty-string fallback only keeps a filter-free call site (a direct handler test)
    /// from passing a null into the command, where it would compare as a mismatch and answer 412.
    /// </remarks>
    private static async Task<IResult> PatchAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        UpdateSeriesRequest request,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        if (request.Language is not null)
        {
            // [DECISION] The language cannot change after creation (plan §3): the member exists on the
            // wire only so that sending it is a documented 400 instead of a silently ignored field.
            return ProblemDetailsWriter.ToProblem(
                [Errors.ValidationFailed("language", "The series language cannot change after creation.")],
                http);
        }

        // Only a string, an explicit null or an absent member is a story preset; anything else (a
        // number, an object, a list, a boolean) is a 400 rather than a silently dropped value.
        if (request.StoryPreset.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
        {
            return ProblemDetailsWriter.ToProblem(
                [Errors.ValidationFailed("storyPreset", "The story preset must be a string or null.")],
                http);
        }

        var patch = BuildPatch(request);
        var storyPresetSpecified = request.StoryPreset.ValueKind != JsonValueKind.Undefined;

        var result = await sender
            .Send(new UpdateSeriesCommand(id, ifMatch ?? string.Empty, patch, storyPresetSpecified), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var etag = await etags.SeriesAsync(id, ct).ConfigureAwait(false);
        if (etag is null)
        {
            // The command committed, so the row exists and the caller owns it: only a bug can reach
            // this. 404 is the honest answer, and it keeps the response out of the unhandled path.
            return ProblemDetailsWriter.ToProblem([Errors.NotFound()], http);
        }

        http.Response.Headers.ETag = etag;

        return Results.Json(new SeriesUpdateResponse(etag), statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>DELETE /api/v1/series/{id}</c>: 204, archiving the series (its episodes stay readable), or
    /// 404 for a missing or foreign one.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender.Send(new ArchiveSeriesCommand(id), ct).ConfigureAwait(false)).ToNoContentResult(http);

    /// <summary>
    /// Turns the bound body into the aggregate's patch.
    /// </summary>
    /// <remarks>
    /// The raw <see cref="JsonElement"/> is what makes plan §5's merge semantics possible: only it can
    /// tell an absent <c>storyPreset</c> (<c>ValueKind.Undefined</c>, which a nullable <c>string</c>
    /// shares with an explicit <c>null</c>) from one the client sent as <c>null</c>, and those two have
    /// different meanings — keep the stored preset, or clear it. The absent case becomes the same
    /// <c>null</c> here and the handler replaces it with the current value, which is what "keep" means
    /// for a patch.
    /// </remarks>
    /// <param name="request">The bound body, whose preset member was already checked as string/null/absent.</param>
    /// <returns>The patch the command carries.</returns>
    private static SeriesPatch BuildPatch(UpdateSeriesRequest request) => new(
        request.Title,
        request.Topic,
        request.Aspect,
        request.VoiceId,
        request.StyleTemplateId,
        request.StoryPreset.ValueKind == JsonValueKind.String ? request.StoryPreset.GetString() : null);
}

/// <summary>
/// The <c>PATCH /api/v1/series/{id}</c> body (09.4.1 plan §3/§5): the JSON-merge patch plus the one
/// member that may be sent but never changed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Language"/> is not part of <see cref="SeriesPatch"/> — the aggregate has no way to change
/// it — but it is part of the wire model, so a client that tries gets 400 <c>validation_failed</c> with
/// field <c>language</c> instead of a field that silently vanishes.
/// </para>
/// <para>
/// <see cref="StoryPreset"/> is the raw JSON value on purpose; see the endpoint's mapping for the
/// absent / null / value distinction (plan §5).
/// </para>
/// </remarks>
/// <param name="Title">The new title, or <see langword="null"/> to keep the current one.</param>
/// <param name="Topic">The new topic, or <see langword="null"/> to keep the current one.</param>
/// <param name="Language">Never accepted; sending it is a 400.</param>
/// <param name="Aspect">The new aspect, or <see langword="null"/> to keep the current one.</param>
/// <param name="VoiceId">The new voice, or <see langword="null"/> to keep the current one.</param>
/// <param name="StyleTemplateId">The new style template, or <see langword="null"/> to keep the current one.</param>
/// <param name="StoryPreset">The new preset key, <c>null</c> to clear it, or absent to keep it.</param>
public sealed record UpdateSeriesRequest(
    string? Title,
    Topic? Topic,
    Language? Language,
    Aspect? Aspect,
    string? VoiceId,
    Guid? StyleTemplateId,
    JsonElement StoryPreset);

/// <summary>
/// The frozen <c>PATCH /api/v1/series/{id}</c> success body (09.4.1 plan §5): <c>{ etag }</c>, the same
/// value as the <c>ETag</c> header, so a client that only reads bodies still learns the tag it must
/// send with its next <c>If-Match</c> (09 README D15).
/// </summary>
/// <param name="Etag">The quoted strong ETag of the committed row.</param>
public sealed record SeriesUpdateResponse(string Etag);
