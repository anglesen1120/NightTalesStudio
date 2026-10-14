using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Commands.UpdateBible;
using NightTales.Application.Features.SeriesBible.Queries.GetBible;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The series-bible routes of one series (02.6.3 plan §4 U1/U2, §5): read the <c>bible.v1</c> document and its
/// pending proposal, and replace the document. Its ETag is <c>"sb-&lt;version&gt;"</c> (02 README D9), a strong tag
/// of the row's own <c>version</c> column — not an <c>xmin</c>, so nothing else may add a second concurrency token.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are thin (09.1.2-T1): bind, send one message through the D4 pipeline, answer. Another tenant's
/// series, an unknown id and a deleted series all answer 404 from the tenant-filtered lookup, never 403. Every
/// message is denied by default, so an anonymous caller answers 401 <c>unauthorized</c> before a handler runs.
/// </para>
/// <para>
/// <c>PUT</c> takes the required <c>If-Match</c> (<see cref="RequireIfMatchExtensions.RequireIfMatch"/> — the 428
/// <c>if_match_required</c> before the handler) and no <c>Idempotency-Key</c>: the ETag is the concurrency token, so
/// a replay is a 412, never a second write. Dismissing and accepting a pending proposal belong to 02.6.3-T2 and are
/// not mapped here.
/// </para>
/// <para>
/// The read publishes the tag from the success value itself (<c>BibleDto.ETag</c>, <see cref="IHasETag"/>), so
/// <see cref="ResultExtensions.ToHttpResult{T}(ErrorOr{T}, HttpContext)"/> sets the <c>ETag</c> header and the body
/// carries the identical quoted value the client sends back as its next <c>If-Match</c>. The write does not: its
/// frozen success body names the version <em>without</em> the quotes (<see cref="BibleUpdateResponse"/>), so the
/// header is set here and the body is built from the same value (02.6.3-T1 acceptance).
/// </para>
/// </remarks>
public static class SeriesBibleEndpoints
{
    /// <summary>The group's prefix; the read and write routes of one series' bible hang under it.</summary>
    public const string GroupPrefix = "/api/v1/series/{id:guid}/bible";

    /// <summary>Reads the bible of the series in the route.</summary>
    public const string GetRoute = GroupPrefix;

    /// <summary>Replaces the bible of the series in the route; the same address as the read with <c>PUT</c>.</summary>
    public const string PutRoute = GroupPrefix;

    /// <summary>
    /// The tag the routes carry (a plain name, never a dotted one: <c>ng-openapi-gen</c> cannot group a dotted tag).
    /// </summary>
    public const string Tag = "SeriesBible";

    /// <summary>Maps the series-bible routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapSeriesBibleEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var bible = routes.MapGroup(GroupPrefix)
            .RequireAuthorization()
            .WithTags(Tag)
            .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        bible.MapGet(string.Empty, GetAsync)
            .Produces<BibleDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        bible.MapPut(string.Empty, UpdateAsync)
            .RequireIfMatch()
            .Produces<BibleUpdateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/v1/series/{id}/bible</c>: 200 with the current version, the <c>ETag: "sb-&lt;version&gt;"</c> and
    /// <c>proposal</c> (null until 02.6.3-T2 writes one), or the documented 401/404/500.
    /// </summary>
    /// <remarks>
    /// The read never writes: a series that exists without a <c>series_bibles</c> row is data drift, answered as 500
    /// <c>series_bible_missing</c> (plan §4 U1), never auto-created by a query.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetBibleQuery(id), ct).ConfigureAwait(false);
        if (!result.IsError)
        {
            http.Response.Headers.Vary = "Authorization";
            http.Response.Headers.CacheControl = "no-cache";
        }

        return result.ToHttpResult(http);
    }

    /// <summary>
    /// <c>PUT /api/v1/series/{id}/bible</c>: 200 with the new version and its <c>ETag: "sb-&lt;version&gt;"</c>, or the
    /// documented 400 <c>bible_invalid_content</c> / 404 / 412 <c>bible_conflict</c> / 428 <c>if_match_required</c>.
    /// </summary>
    /// <remarks>
    /// The body is the full <c>bible.v1</c> document under <c>content</c>. A missing or unreadable body gets the same
    /// 400 <c>bible_invalid_content</c> shape as a body that fails the §3.2 limits; the limits themselves are the
    /// Domain validator's, run by the handler so the per-field list survives into the problem's <c>errors</c> group.
    /// Server-derived summary fields (<c>structure</c>, <c>approved_at</c>) are never read from the request.
    /// </remarks>
    private static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateBibleRequest? request,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        if (request?.Content is null)
        {
            return ProblemDetailsWriter.ToProblem(
                [Error.Validation(
                    ErrorCodes.BibleInvalidContent,
                    "The body is not a bible.v1 document.",
                    new Dictionary<string, object> { [ErrorMetadata.Field] = "body" })],
                http);
        }

        var result = await sender
            .Send(new UpdateBibleCommand(id, request.Content.ToDomain(), ifMatch), ct)
            .ConfigureAwait(false);
        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        // The handler's tag is the strong quoted one (IHasETag); the header publishes it unchanged while the body
        // names the same version without quotes, the success shape the task's acceptance fixes (02.6.3-T1:121).
        var value = result.Value;
        var quoted = value.ETag.StartsWith('"') ? value.ETag : $"\"{value.ETag}\"";
        http.Response.Headers.ETag = quoted;

        return Results.Json(
            new BibleUpdateResponse(value.Version, quoted.Trim('"')),
            statusCode: StatusCodes.Status200OK);
    }
}

/// <summary>
/// The <c>PUT /api/v1/series/{id}/bible</c> body (02.6.3 plan §5): the <c>bible.v1</c> document under
/// <c>content</c>, so the request component references the same <c>BibleContentV1</c> schema the read answers.
/// </summary>
/// <param name="Content">The replacement document; <c>schema_version</c> is written by the backend, never read.</param>
public sealed record UpdateBibleRequest(BibleContentV1 Content);

/// <summary>
/// The frozen <c>PUT /api/v1/series/{id}/bible</c> success body (02.6.3-T1 acceptance): <c>{ version, etag }</c> —
/// the version the write produced and the tag <em>without</em> the quotes the <c>ETag</c> header carries, so a
/// client that reads only bodies still learns which version it now holds and may send either form back.
/// </summary>
/// <param name="Version">The <c>series_bibles.version</c> after the replace.</param>
/// <param name="Etag">The new tag without quotes (<c>sb-2</c>), while the header says <c>"sb-2"</c>.</param>
public sealed record BibleUpdateResponse(int Version, string Etag);
