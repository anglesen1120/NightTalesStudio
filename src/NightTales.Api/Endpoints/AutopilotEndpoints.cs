using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Commands.AcceptAutopilotAuthorization;
using NightTales.Application.Features.Autopilot.Commands.PauseAutopilot;
using NightTales.Application.Features.Autopilot.Commands.PutAutopilot;
using NightTales.Application.Features.Autopilot.Commands.ResumeAutopilot;
using NightTales.Application.Features.Autopilot.Commands.RevokeAutopilotAuthorization;
using NightTales.Application.Features.Autopilot.Dtos;
using NightTales.Application.Features.Autopilot.Queries.GetAutopilot;
using NightTales.Application.Features.Autopilot.Queries.ListAutopilotRuns;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The autopilot routes of one series (02.8.3 plan §4 UC1–UC7, §5, task §Routes): read the state and the rendered
/// authorization text, save the settings and the switch, accept or revoke the authorization, pause and resume, and
/// list the runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thin, like every other endpoint group</b> (09.1.2-T1): bind, send one message through the D4 pipeline, map the
/// <c>ErrorOr</c> answer. Two things are this file's alone — the two request bodies it reads under a cap
/// (<see cref="BoundedBodyReader"/>) and the post-commit <c>ETag</c> read (<see cref="PostCommitETagReader"/>, 09 README
/// D15) — because only the Api knows what the client sent and what the response headers are.
/// </para>
/// <para>
/// <b>The ETag is <c>"sa-&lt;series.xmin&gt;"</c></b> (02 README D9, plan §10 open item 7). The settings of the autopilot
/// are columns of the <c>series</c> row, so the tag comes from the same <c>xmin</c> the series route uses, with its own
/// prefix so the two can never be replayed on each other. <c>PUT</c> requires it (<c>If-Match</c>, 428
/// <c>if_match_required</c> from the filter, 412 <c>series_conflict</c> from the handler); the read publishes it and
/// every mutating route republishes it after its unit of work committed, because <c>xmin</c> is assigned at commit.
/// </para>
/// <para>
/// <b>Every route is behind <c>RequireFeature("youtube_publish")</c></b> (06 README D9, attached per route exactly as the
/// publication routes do): with the flag off an authenticated caller answers 404 <c>feature_disabled</c> before a
/// handler runs, which is also how the whole feature is hidden by a rollback without a migration (task §Failure
/// handling). Authorization is middleware and runs before the endpoint filters, so an anonymous caller answers 401
/// first. A caller who is authenticated but does not own the series gets 404 from the tenant-filtered lookup, never 403
/// (09 README D10).
/// </para>
/// <para>
/// <b>The two bodies are read here and not bound.</b> <c>PUT</c> carries <c>{ enabled, settings }</c> where
/// <c>settings</c> is the owner's document, and the command must receive that document <em>verbatim</em> (its key order
/// included: the domain <c>AutopilotSettingsValidator</c> validates it and the covered-field comparator later diffs it), so
/// the endpoint reads the capped bytes and passes <c>settings.GetRawText()</c> on. The 16 KiB cap is S71-48's, declared
/// twice — as endpoint metadata for the middleware and Kestrel, and in the bounded read for a streamed body that grows
/// past it — which is what makes it hold in a test host too.
/// </para>
/// <para>
/// No route carries <c>RequireIdempotencyKey()</c>: none of them spends money or calls out, and the <c>If-Match</c> tag is
/// the replay guard of the one route that writes settings.
/// </para>
/// </remarks>
public static class AutopilotEndpoints
{
    /// <summary>The group's prefix: the autopilot of one series, under the series resource.</summary>
    public const string GroupPrefix = "/api/v1/series/{id:guid}/autopilot";

    /// <summary>The read and the settings write of the group.</summary>
    public const string SettingsRoute = GroupPrefix;

    /// <summary>The accept (<c>POST</c>) and revoke (<c>DELETE</c>) route of the versioned authorization.</summary>
    public const string AuthorizationRoute = GroupPrefix + "/authorization";

    /// <summary>The owner's pause button (plan §3.6 row 15).</summary>
    public const string PauseRoute = GroupPrefix + "/pause";

    /// <summary>The owner's resume button (plan §3.6 "Resume").</summary>
    public const string ResumeRoute = GroupPrefix + "/resume";

    /// <summary>The run list with its keyset cursor.</summary>
    public const string RunsRoute = GroupPrefix + "/runs";

    /// <summary>
    /// The tag the routes carry (a plain name, never a dotted one: <c>ng-openapi-gen</c> cannot group a dotted tag).
    /// </summary>
    public const string Tag = "Autopilot";

    /// <summary>
    /// The largest request body these routes accept, in bytes (16 KiB, S71-48): the §3.1 settings document is a dozen
    /// fields and the pause note is one line, so anything past this is a client defect rather than a bigger document.
    /// </summary>
    public const int MaxBodyBytes = 16 * 1_024;

    /// <summary>
    /// The 400 description a body of the wrong media type answers (the guard both raw-body readers share:
    /// <see cref="StoryEndpoints"/> and <c>VideoReviewEndpoints</c>).
    /// </summary>
    private const string JsonBodyDescription = "The request body must be a JSON object.";

    /// <summary>
    /// The feature flag that gates every route (06 README D9); the same key the publishing routes carry, declared once
    /// in <see cref="Publishing.YouTubeEtaEndpoints"/>.
    /// </summary>
    public const string FeatureKey = Publishing.YouTubeEtaEndpoints.FeatureKey;

    /// <summary>Maps the autopilot routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapAutopilotEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy; every route here is called
        // by the SPA with a bearer token, so their preflights need it too.
        var autopilot = routes.MapGroup(GroupPrefix)
            .RequireAuthorization()
            .WithTags(Tag)
            .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        autopilot.MapGet(string.Empty, GetAsync)
            .RequireFeature(FeatureKey)
            .Produces<AutopilotDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        autopilot.MapPut(string.Empty, PutAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .RequireFeature(FeatureKey)
            .RequireIfMatch()
            .Accepts<PutAutopilotRequest>("application/json")
            .Produces<AutopilotEtagResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        autopilot.MapPost("/authorization", AcceptAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .RequireFeature(FeatureKey)
            .Produces<AcceptedAuthorizationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        // The revoke takes no If-Match: it is the brake of the group (plan §3.7), it is idempotent when nothing is
        // current, and refusing it on a stale tag would leave an authorization the owner wants gone in place.
        autopilot.MapDelete("/authorization", RevokeAsync)
            .RequireFeature(FeatureKey)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Neither owner button takes an If-Match or an Idempotency-Key: both are idempotent by state (a second pause or
        // resume writes nothing), and both are the answer to an emergency, so a stale tag must not be able to block
        // them (plan §3.6 rows 15/16 and "Resume").
        autopilot.MapPost("/pause", PauseAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .RequireFeature(FeatureKey)
            .Accepts<PauseAutopilotRequest>(isOptional: true, "*/*")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        autopilot.MapPost("/resume", ResumeAsync)
            .RequireFeature(FeatureKey)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        autopilot.MapGet("/runs", ListRunsAsync)
            .RequireFeature(FeatureKey)
            .Produces<AutopilotRunPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/v1/series/{id}/autopilot?locale=</c>: 200 with the series' autopilot state, the settings, the current
    /// authorization, the rendered authorization text for that locale and the next three slots, plus the
    /// <c>ETag: "sa-&lt;xmin&gt;"</c> header.
    /// </summary>
    /// <remarks>
    /// The tag is read after the handler answered even though the read writes nothing: the reader is the single place a
    /// route learns the current tag (09 README D15), and it keeps the header's source identical to the mutating routes'
    /// — the body itself carries no <c>etag</c> member (task §DTOs), so the header is the only place the client learns it.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        Guid id,
        [FromQuery(Name = "locale")] string? locale,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
        => await (await sender.Send(new GetAutopilotQuery(id, locale), ct).ConfigureAwait(false))
            .ToHttpResult(http, (_, token) => etags.SeriesAutopilotAsync(id, token))
            .ConfigureAwait(false);

    /// <summary>
    /// <c>PUT /api/v1/series/{id}/autopilot</c>: 200 with the new tag in the <c>ETag</c> header and in the body, or the
    /// documented 400/401/404/409/412/413/428.
    /// </summary>
    /// <remarks>
    /// The <c>If-Match</c> filter answered 428 before this method ran, so <paramref name="ifMatch"/> is present; the
    /// empty-string fallback only keeps a filter-free call site (a direct handler test) from passing a null into the
    /// command, where it would compare as a mismatch and answer 412. The order of the answers inside the command is the
    /// handler's (404 → 412 → the enable checks), so a body this route cannot read is refused before it, as 400
    /// <c>validation_failed</c> or 413 <c>payload_too_large</c>.
    /// </remarks>
    private static async Task<IResult> PutAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var raw = await BoundedBodyReader.ReadAsync(http, MaxBodyBytes, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return ProblemDetailsWriter.ToProblem([ProblemDetailsWriter.PayloadTooLargeError], http);
        }

        // The route declares its request body but reads it here, so routing's own 415 never fires (StoryEndpoints and
        // VideoReviewEndpoints guard the same seam): a text/plain body that happens to parse as JSON is refused rather
        // than accepted, which keeps the declared content type honest.
        if (!http.Request.HasJsonContentType())
        {
            return ProblemDetailsWriter.ToProblem([Errors.ValidationFailed("body", JsonBodyDescription)], http);
        }

        if (TryReadSettingsBody(raw.Value, out var enabled, out var settingsJson) is { } bodyError)
        {
            return ProblemDetailsWriter.ToProblem([bodyError], http);
        }

        var result = await sender
            .Send(new PutAutopilotCommand(id, ifMatch ?? string.Empty, enabled, settingsJson), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        return await WriteEtagResponseAsync(id, etags, http, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/v1/series/{id}/autopilot/authorization</c>: 201 with <c>{ version, acceptedAt }</c>, or the documented
    /// 400 <c>autopilot_settings_invalid</c> / 404 / 409 <c>autopilot_authorization_required</c> (with
    /// <c>errors.textHash = stale_text</c> or <c>errors.textVersion</c>).
    /// </summary>
    /// <remarks>
    /// The body is D-6's pair only — <c>{ textVersion, textHash }</c> — and the handler re-renders both locales from the
    /// <em>stored</em> settings, so a page rendered before a settings change cannot be accepted (task §Failure handling).
    /// The response republishes the series tag as the header: accepting moves the series' pointer, so the client's next
    /// <c>If-Match</c> must come from here rather than from the page it read before.
    /// </remarks>
    private static async Task<IResult> AcceptAsync(
        Guid id,
        [FromBody] AcceptAutopilotAuthorizationRequest? request,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender
            .Send(
                new AcceptAutopilotAuthorizationCommand(
                    id,
                    request?.TextVersion ?? string.Empty,
                    request?.TextHash ?? string.Empty),
                ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        if (await etags.SeriesAutopilotAsync(id, ct).ConfigureAwait(false) is { } etag)
        {
            http.Response.Headers.ETag = etag;
        }

        return Results.Json(result.Value, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// <c>DELETE /api/v1/series/{id}/autopilot/authorization</c>: 204, or 404 for a missing or foreign series.
    /// </summary>
    /// <remarks>
    /// The command revokes the current revision with <c>owner_revoked</c> and clears the pointer; it is idempotent when
    /// nothing is current (D-1/§3.7). The route republishes the tag because the pointer write bumps the series' <c>xmin</c>.
    /// </remarks>
    private static async Task<IResult> RevokeAsync(
        Guid id,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
        => await (await sender
                .Send(new RevokeAutopilotAuthorizationCommand(id), ct)
                .ConfigureAwait(false))
            .ToNoContentResult(http, (_, token) => etags.SeriesAutopilotAsync(id, token))
            .ConfigureAwait(false);

    /// <summary>
    /// <c>POST /api/v1/series/{id}/autopilot/pause</c>: 204, or 404 for a missing or foreign series.
    /// </summary>
    /// <remarks>
    /// The body is the optional <c>{ note? }</c> of plan §5; it is accepted and not stored (no column holds it, and D8
    /// forbids free text in an audit entry — 02.8.3-T4 owns what the note becomes), so an empty body answers 204 exactly
    /// as <c>{}</c> does.
    /// </remarks>
    private static async Task<IResult> PauseAsync(
        Guid id,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var raw = await BoundedBodyReader.ReadAsync(http, MaxBodyBytes, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return ProblemDetailsWriter.ToProblem([ProblemDetailsWriter.PayloadTooLargeError], http);
        }

        // The body is optional here, so the content type is only checked when one arrived: a body-less pause is the
        // documented 204 path and must not be refused for the header it does not need.
        if (!raw.Value.IsEmpty && !http.Request.HasJsonContentType())
        {
            return ProblemDetailsWriter.ToProblem([Errors.ValidationFailed("body", JsonBodyDescription)], http);
        }

        if (TryReadPauseBody(raw.Value, out var note) is { } bodyError)
        {
            return ProblemDetailsWriter.ToProblem([bodyError], http);
        }

        return await (await sender
                .Send(new PauseAutopilotCommand(id, note), ct)
                .ConfigureAwait(false))
            .ToNoContentResult(http, (_, token) => etags.SeriesAutopilotAsync(id, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/v1/series/{id}/autopilot/resume</c>: 204, or the documented 404 / 409
    /// <c>autopilot_authorization_required</c> / 409 <c>channel_reauth_required</c>.
    /// </summary>
    /// <remarks>
    /// The §3.6 trigger ladder that answers 409 <c>autopilot_pause_reason_active</c> is 02.8.3-T4's; this route maps
    /// whatever the handler answers, so T4 adds no route changes.
    /// </remarks>
    private static async Task<IResult> ResumeAsync(
        Guid id,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
        => await (await sender
                .Send(new ResumeAutopilotCommand(id), ct)
                .ConfigureAwait(false))
            .ToNoContentResult(http, (_, token) => etags.SeriesAutopilotAsync(id, token))
            .ConfigureAwait(false);

    /// <summary>
    /// <c>GET /api/v1/series/{id}/autopilot/runs?cursor=&amp;limit=</c>: 200 with <c>{ items, nextCursor }</c>, or 400
    /// <c>validation_failed</c> for a bad cursor or limit.
    /// </summary>
    /// <remarks>
    /// The two values are bound one by one and not as one [AsParameters] record: the absent limit has a documented
    /// default the handler applies (20), and the route must pass <see langword="null"/> rather than invent a number.
    /// </remarks>
    private static async Task<IResult> ListRunsAsync(
        Guid id,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] int? limit,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
                .Send(new ListAutopilotRunsQuery(id, cursor, limit), ct)
                .ConfigureAwait(false))
            .ToHttpResult(http);

    /// <summary>Reads the tag after the commit and answers it in the header and in the body (09 README D15).</summary>
    /// <param name="id">The series the routes address.</param>
    /// <param name="etags">The post-commit reader.</param>
    /// <param name="http">The request whose header is set.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The 200 with <see cref="AutopilotEtagResponse"/>, or 404 when the row is not visible.</returns>
    private static async Task<IResult> WriteEtagResponseAsync(
        Guid id,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var etag = await etags.SeriesAutopilotAsync(id, ct).ConfigureAwait(false);
        if (etag is null)
        {
            // The command committed, so the row exists and the caller owns it: only a bug can reach this. 404 is the
            // honest answer, and it keeps the response out of the unhandled path (the SeriesEndpoints rule).
            return ProblemDetailsWriter.ToProblem([Errors.NotFound()], http);
        }

        http.Response.Headers.ETag = etag;

        return Results.Json(new AutopilotEtagResponse(etag), statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// Reads the <c>PUT</c> body: <c>{ enabled, settings }</c>, with the settings document handed on verbatim.
    /// </summary>
    /// <param name="raw">The bounded bytes of the body.</param>
    /// <param name="enabled">The switch.</param>
    /// <param name="settingsJson">The settings document exactly as the client wrote it.</param>
    /// <returns>The 400 to answer, or <see langword="null"/> when the body is usable.</returns>
    private static Error? TryReadSettingsBody(ReadOnlyMemory<byte> raw, out bool enabled, out string settingsJson)
    {
        enabled = false;
        settingsJson = string.Empty;

        if (raw.IsEmpty)
        {
            return Errors.ValidationFailed("body", "The body must be an object with `enabled` and `settings`.");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Errors.ValidationFailed("body", "The body must be an object with `enabled` and `settings`.");
            }

            if (!body.TryGetProperty("enabled", out var enabledValue)
                || enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Errors.ValidationFailed("enabled", "`enabled` must be a boolean.");
            }

            // The document is passed on as the client wrote it: the domain validator owns its shape and reports its
            // errors keyed by JSON path, and the covered-field comparator later diffs exactly this text.
            if (!body.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            {
                return Errors.ValidationFailed("settings", "`settings` must be an object.");
            }

            enabled = enabledValue.GetBoolean();
            settingsJson = settings.GetRawText();

            return null;
        }
        catch (JsonException)
        {
            return Errors.ValidationFailed("body", "The body is not valid JSON.");
        }
    }

    /// <summary>Reads the optional <c>pause</c> body: <c>{ note? }</c>, and an absent body as "no note".</summary>
    /// <param name="raw">The bounded bytes of the body.</param>
    /// <param name="note">The owner's note, or <see langword="null"/>.</param>
    /// <returns>The 400 to answer, or <see langword="null"/> when the body is usable.</returns>
    private static Error? TryReadPauseBody(ReadOnlyMemory<byte> raw, out string? note)
    {
        note = null;

        if (raw.IsEmpty || IsNullLiteral(raw.Span))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Errors.ValidationFailed("body", "The body must be an object with an optional `note`.");
            }

            if (!body.TryGetProperty("note", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                return Errors.ValidationFailed("note", "`note` must be a string.");
            }

            note = value.GetString();

            return null;
        }
        catch (JsonException)
        {
            return Errors.ValidationFailed("body", "The body is not valid JSON.");
        }
    }

    /// <summary>Whether the body is the JSON <c>null</c> literal, which a "no body" client sometimes sends.</summary>
    private static bool IsNullLiteral(ReadOnlySpan<byte> raw) =>
        raw.Trim(" \t\r\n"u8).SequenceEqual("null"u8);
}

/// <summary>
/// The optional <c>POST /api/v1/series/{id}/autopilot/pause</c> body (plan §5: <c>{ note? }</c>). Declared for the
/// OpenAPI document only — the endpoint reads the capped bytes so an absent body is a legal 204.
/// </summary>
/// <param name="Note">The owner's optional note; accepted and not stored (D8 keeps free text out of audit entries).</param>
public sealed record PauseAutopilotRequest(string? Note = null);

/// <summary>
/// The <c>PUT /api/v1/series/{id}/autopilot</c> body (02.8.3 plan §5, task §Routes): the switch and the settings
/// document.
/// </summary>
/// <remarks>
/// The endpoint does not bind this record — it reads the bytes under the cap and hands <c>settings</c> on verbatim, so
/// the document keeps its key order — but the route declares it with <c>.Accepts&lt;PutAutopilotRequest&gt;</c> so the
/// generated OpenAPI document names the request body it actually accepts (10 CR: the schema is the contract the web
/// client generates from).
/// </remarks>
/// <param name="Enabled">Whether autopilot should be on; enabling needs the checks of plan §3.7 step (0)–(5).</param>
/// <param name="Settings">The <c>autopilot_settings</c> document of plan §3.1.</param>
public sealed record PutAutopilotRequest(bool Enabled, JsonElement Settings);

/// <summary>
/// The <c>POST /api/v1/series/{id}/autopilot/authorization</c> body (D-6): the version and the hash of the text the
/// owner read. The server re-renders both locales from the stored settings and accepts only an exact hash.
/// </summary>
/// <param name="TextVersion">The template version the page rendered, e.g. <c>autopilot-auth.v1</c>.</param>
/// <param name="TextHash">The lowercase hex SHA-256 of the rendered text the owner accepted.</param>
public sealed record AcceptAutopilotAuthorizationRequest(string TextVersion, string TextHash);

/// <summary>
/// The frozen <c>PUT /api/v1/series/{id}/autopilot</c> success body (task §Notes: the tag must be in the body
/// <em>and</em> in the header so the UI can re-use it without a second read): <c>{ etag }</c>, byte-identical to the
/// header.
/// </summary>
/// <param name="Etag">The quoted strong ETag of the committed row (<c>"sa-&lt;xmin&gt;"</c>).</param>
public sealed record AutopilotEtagResponse(string Etag);
