using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Commands.AcceptBibleProposal;
using NightTales.Application.Features.SeriesBible.Commands.DismissBibleProposal;
using NightTales.Application.Features.SeriesBible.Commands.UpdateBible;
using NightTales.Application.Features.SeriesBible.Queries.GetBible;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The series-bible routes of one series (02.6.3 plan §4 U1/U2/U3/U4, §5): read the <c>bible.v1</c> document and its
/// pending proposal, replace the document, and resolve the proposal by accepting or dismissing it. Its ETag is
/// <c>"sb-&lt;version&gt;"</c> (02 README D9), a strong tag of the row's own <c>version</c> column — not an
/// <c>xmin</c>, so nothing else may add a second concurrency token.
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
/// a replay is a 412, never a second write.
/// </para>
/// <para>
/// The two proposal routes (02.6.3-T2) are the only <c>POST</c>s of the group and neither carries an <c>If-Match</c>
/// or a body: the proposal is the stored row's, the ops are append-only, and a second call of either answers 409
/// <c>bible_proposal_not_pending</c> rather than a silent no-op. The accept answers the same
/// <c>{ version, etag }</c> shape the save does — it produces a content version — while the dismiss answers 204
/// because it produces none.
/// </para>
/// <para>
/// The read publishes the tag from the success value itself (<c>BibleDto.ETag</c>, <see cref="IHasETag"/>), so
/// <see cref="ResultExtensions.ToHttpResult{T}(ErrorOr.ErrorOr{T}, HttpContext)"/> sets the <c>ETag</c> header and the body
/// carries the identical quoted value the client sends back as its next <c>If-Match</c>. The two writes do the same:
/// their success body names the version and the tag byte-identical to the header
/// (<see cref="BibleUpdateResponse"/>), so either body and header is the exact <c>If-Match</c> the next save needs.
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

    /// <summary>Accepts the pending proposal of the series' bible (02.6.3-T2, plan §4 U4).</summary>
    public const string AcceptProposalRoute = GroupPrefix + "/proposal/accept";

    /// <summary>Dismisses the pending proposal of the series' bible (02.6.3-T2, plan §4 U3).</summary>
    public const string DismissProposalRoute = GroupPrefix + "/proposal/dismiss";

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

        // Neither proposal route takes an If-Match: the ops are append-only, so an accept cannot conflict with a
        // manual save, and a dismissal cannot damage anything (plan §4 U3/U4). Both are free (no ledger, no budget)
        // and neither carries an Idempotency-Key — a repeat is answered 409 by the handler, which is the answer a
        // client can act on: reload the bible and see who moved it.
        bible.MapPost("/proposal/accept", AcceptProposalAsync)
            .Produces<BibleUpdateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        bible.MapPost("/proposal/dismiss", DismissProposalAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

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
    /// The body is the full <c>bible.v1</c> document under <c>content</c>, or a body that carries no document at all:
    /// a missing <c>content</c> is passed through to the handler, which owns that 400 <c>bible_invalid_content</c> in
    /// its own order, so a foreign series is still 404 and a stale tag still 412 even without a document. Only an
    /// unreadable body is refused earlier, by the binder as 400 <c>validation_failed</c>. The limits themselves are
    /// the Domain validator's, run by the handler so the per-field list survives into the problem's <c>errors</c>
    /// group. Server-derived summary fields (<c>structure</c>, <c>approved_at</c>) are never read from the request.
    /// </remarks>
    private static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateBibleRequest? request,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        // A body that carries no document is not refused here: the handler owns that 400 (bible_invalid_content),
        // so it runs after the series is known to be the caller's own (404) and after the tag is the current version
        // (428/412) — a missing document never hides the real reason a save is impossible. An unreadable body is
        // still the binder's 400 validation_failed, refused before any handler.
        var result = await sender
            .Send(new UpdateBibleCommand(id, request?.Content?.ToDomain(), ifMatch), ct)
            .ConfigureAwait(false);
        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        // The handler's tag is the strong quoted one (IHasETag); the header publishes it and the body carries the
        // byte-identical value, so a client that reads only the body learns the exact tag it must send back as its
        // next If-Match (02.6.3-T1 acceptance, README D9, the FactSheetDtos IHasETag precedent).
        var value = result.Value;
        var quoted = value.ETag.StartsWith('"') ? value.ETag : $"\"{value.ETag}\"";
        http.Response.Headers.ETag = quoted;

        return Results.Json(
            new BibleUpdateResponse(value.Version, quoted),
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>POST /api/v1/series/{id}/bible/proposal/accept</c> (02.6.3-T2, plan §4 U4): 200 with the version the
    /// applied ops produced and its <c>ETag: "sb-&lt;version&gt;"</c>, or the documented 401/404/409
    /// <c>bible_proposal_not_pending</c>.
    /// </summary>
    /// <remarks>
    /// The body is the same <c>{ version, etag }</c> the save answers with, and the two are byte-identical: the
    /// accept produces a content version exactly as a save does, so the client's next <c>If-Match</c> comes from the
    /// same place. No <c>If-Match</c> is required and no body is read — the ops and the source episode are the
    /// stored row's, never the request's.
    /// </remarks>
    private static async Task<IResult> AcceptProposalAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new AcceptBibleProposalCommand(id), ct).ConfigureAwait(false);
        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;
        var quoted = value.ETag.StartsWith('"') ? value.ETag : $"\"{value.ETag}\"";
        http.Response.Headers.ETag = quoted;

        return Results.Json(
            new BibleUpdateResponse(value.Version, quoted),
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>POST /api/v1/series/{id}/bible/proposal/dismiss</c> (02.6.3-T2, plan §4 U3): 204 with no body and no
    /// version change, or the documented 401/404/409 <c>bible_proposal_not_pending</c>.
    /// </summary>
    /// <remarks>
    /// A dismissal writes no content version, so the route publishes no tag: the client re-reads the bible to learn
    /// the version it still has. Nothing is read from the request, and the success carries no payload.
    /// </remarks>
    private static async Task<IResult> DismissProposalAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct) =>
        (await sender.Send(new DismissBibleProposalCommand(id), ct).ConfigureAwait(false)).ToNoContentResult(http);
}

/// <summary>
/// The <c>PUT /api/v1/series/{id}/bible</c> body (02.6.3 plan §5): the <c>bible.v1</c> document under
/// <c>content</c>, so the request component references the write schema <c>BibleContentV1</c>, which never
/// advertises the summary members the server owns. The read answers <c>BibleContentResponseV1</c>, the same
/// document with <c>structure</c> and <c>approved_at</c> filled in.
/// </summary>
/// <param name="Content">The replacement document; <c>schema_version</c> is written by the backend, never read.</param>
public sealed record UpdateBibleRequest(BibleContentV1 Content);

/// <summary>
/// The frozen <c>PUT /api/v1/series/{id}/bible</c> success body (02.6.3-T1 acceptance): <c>{ version, etag }</c> —
/// the version the write produced and the tag byte-identical to the <c>ETag</c> header, quotes included, so a
/// client that reads only bodies still learns the exact tag it must send back as its next <c>If-Match</c>.
/// </summary>
/// <param name="Version">The <c>series_bibles.version</c> after the replace.</param>
/// <param name="Etag">The new tag with quotes (<c>"sb-2"</c>), the same value the header carries.</param>
public sealed record BibleUpdateResponse(int Version, string Etag);
