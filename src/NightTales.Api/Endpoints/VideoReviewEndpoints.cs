using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NightTales.Api.Contracts.VideoReview;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.VideoReview.Commands.ApproveVideo;
using NightTales.Application.Features.VideoReview.Commands.RejectVideo;
using NightTales.Domain.Episodes;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The video-review routes of an episode (09 README BD1, 09.7.1 plan §Design): checkpoint #2, where the owner
/// accepts the rendered video or rejects it, and where the reject chooses the way back.
/// </summary>
/// <remarks>
/// <para>
/// Both routes are thin (09.1.2-T1): bind, send one message through the D4 pipeline, answer the new state. The
/// <c>ETag</c> header and the body's <c>etag</c> are the same post-commit <c>"ep-&lt;xmin&gt;"</c> tag, read
/// through <see cref="PostCommitETagReader"/> (09 README D15). Another tenant's episode is a 404 from the
/// tenant-filtered lookup, never 403 (D10).
/// </para>
/// <para>
/// Both routes require an <c>If-Match</c> (D9/D15, 428 when absent) and neither spends anything in-process, so
/// neither carries the <c>paid</c> rate-limit policy ([DECISION] plan §Notes, BD14). Reject also requires an
/// <c>Idempotency-Key</c> (D6): its spend happens later in the Worker, and a repeated POST must not reject
/// twice — the stored result is replayed, target included, so the replayed answer keeps the first request's
/// status and its 200 or 202 choice; the <c>etag</c> it carries, however, is read after commit at replay time,
/// so it is the current tag, not necessarily the one the first response had.
/// </para>
/// <para>
/// The reject answers 200 for the <c>story</c> target (<c>video_review → story_review</c>) and 202 with
/// <b>no</b> <c>Location</c> for <c>re_render</c> (<c>video_review → story_approved</c>): at response time no
/// render job exists yet — the Worker's outbox handler starts it outside the rejecting transaction (WT3) — so
/// there is nothing for a status URI to point at.
/// </para>
/// </remarks>
public static class VideoReviewEndpoints
{
    /// <summary>Accepts the rendered final video and moves the episode to <c>ready</c> (WT56).</summary>
    public const string ApproveRoute = $"{EpisodeEndpoints.GroupPrefix}/{{id:guid}}/video/approve";

    /// <summary>Rejects the rendered final video and stores the reason (WT3).</summary>
    public const string RejectRoute = $"{EpisodeEndpoints.GroupPrefix}/{{id:guid}}/video/reject";

    /// <summary>The wire status of an approved episode (09 README D14).</summary>
    private const string ReadyStatus = "ready";

    /// <summary>
    /// The wire serializer of both bodies: the host's web defaults plus the snake_case enum converter
    /// (<c>Program</c> configures the same one for every JSON route), so <c>story</c> and <c>re_render</c> bind
    /// exactly as they do on the framework's own bodies (09 README D14).
    /// </summary>
    /// <remarks>
    /// Integer enum values are refused (<c>allowIntegerValues: false</c>, the outbox serializer's choice): a
    /// numeric <c>target</c> is a member no client can read back, so it is a body error under <c>target</c>
    /// rather than a silent <c>story</c> or <c>re_render</c> (D1).
    /// </remarks>
    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>Maps the video-review routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapVideoReviewEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Both bodies are read by the handlers below (bounded, optional for approve, required for reject), so the
        // routes declare the catch-all media type and let those readers own content-type validation (400), not
        // routing's 415 matcher - the story-approval route's choice (StoryEndpoints.ApproveRoute). The
        // transformer below documents each body under `application/json` again, so the catch-all never reaches
        // the OpenAPI document.
        EpisodeIdRouteParameter.Declare(
            DocumentBodyAsJson(routes.MapPost(ApproveRoute, ApproveAsync)
                .RequireIfMatch()
                .RequireAuthorization()
                .Accepts<ApproveVideoRequest>(isOptional: true, "*/*")
                .Produces<EpisodeStateDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy)));

        EpisodeIdRouteParameter.Declare(
            DocumentBodyAsJson(routes.MapPost(RejectRoute, RejectAsync)
                .RequireIfMatch()
                .RequireIdempotencyKey()
                .RequireAuthorization()
                .Accepts<RejectVideoRequest>("*/*")
                .Produces<EpisodeStateDto>(StatusCodes.Status200OK)
                .Produces<EpisodeStateDto>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy)));

        return routes;
    }

    /// <summary>
    /// Documents a request body declared with the catch-all media type under <c>application/json</c>: the
    /// readers above own content-type validation (400) while the document keeps the JSON body
    /// (StoryEndpoints.ApproveRoute, D4).
    /// </summary>
    /// <param name="route">The mapped route.</param>
    /// <returns>The same builder, for chaining.</returns>
    private static RouteHandlerBuilder DocumentBodyAsJson(RouteHandlerBuilder route) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.RequestBody?.Content is { } content && content.TryGetValue("*/*", out var body))
            {
                content["application/json"] = body;
                content.Remove("*/*");
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/video/approve</c>: 200 with the <c>ready</c> state and its post-commit
    /// <c>ETag</c>, or the documented 400/401/404/409/412/413/428.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is optional: absent, empty and <c>{}</c> are all a valid approval with no note, and a body
    /// that is not a JSON object carrying a string <c>note</c> is 400 <c>validation_failed</c> under the
    /// offending member (<c>body</c> when the root is not a JSON object) — the outcome the story-approval route
    /// maps its invalid body to. Because the body is read here, that 400 precedes the tenant lookup, exactly as
    /// the framework's own binding failure does on every other body route; note length is the command
    /// validator's check (400 <c>validation_failed</c>).
    /// </para>
    /// <para>
    /// The route declares the catch-all media type (<c>*/*</c>), so a body sent as, say, <c>text/plain</c>
    /// reaches this reader and is the same coded 400 — not the framework's empty-bodied 415 (D4). The OpenAPI
    /// document still shows the JSON body.
    /// </para>
    /// <para>
    /// Approve needs no <c>final_video_asset_id</c> guard (task §Failure handling): a hand-inserted
    /// <c>video_review</c> episode answers 200 and the <c>video-url</c> route is the one that 404s without an
    /// asset.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ApproveAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        PostCommitETagReader etags,
        IOptions<RequestLimitsOptions> limits,
        HttpContext http,
        CancellationToken ct)
    {
        var raw = await ReadBodyAsync(http, checked((int)limits.Value.MaxRequestBodySize), ct).ConfigureAwait(false);
        if (raw is null)
        {
            return ProblemDetailsWriter.ToProblem([ProblemDetailsWriter.PayloadTooLargeError], http);
        }

        var body = ReadApproveBody(raw.Value, http.Request);
        if (body.IsError)
        {
            return ProblemDetailsWriter.ToProblem(body.Errors, http);
        }

        var result = await sender
            .Send(new ApproveVideoCommand(id, body.Value.Note, ifMatch), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return result.ToHttpResult(http);
        }

        // D15: the tag is read after the unit of work committed, and the same value is carried in the body
        // and in the header.
        var etag = await etags.EpisodeAsync(id, ct).ConfigureAwait(false);
        ErrorOr<EpisodeStateDto> approved = new EpisodeStateDto(id, ReadyStatus, etag ?? string.Empty);

        return approved.ToHttpResult(http);
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/video/reject</c>: 200 for the <c>story</c> target and 202 without a
    /// <c>Location</c> for <c>re_render</c>, both with the new state and its post-commit <c>ETag</c>, or the
    /// documented 400/401/404/409/412/413/428.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is required and read here (bounded, the approval route's reader) rather than by the framework's
    /// binder, so the route declares the catch-all media type (<c>*/*</c>) and an unknown, numeric, absent or
    /// null <c>target</c> — or any other unreadable body, whatever its content type — is a coded 400
    /// <c>validation_failed</c> problem under the offending member in every environment, never the framework's
    /// empty-bodied 415 (D1, D4). The <c>If-Match</c>/<c>Idempotency-Key</c> filters still answer 428/400 first,
    /// and the OpenAPI document still shows the JSON body.
    /// </para>
    /// <para>
    /// The status is the command's own wire value (<c>story_review</c> or <c>story_approved</c>), and the
    /// 200/202 choice is the stored result's <see cref="RejectTarget"/>, so a replayed
    /// <c>Idempotency-Key</c> keeps the first request's status and its 200 or 202 choice (D6, WT3). The
    /// <c>etag</c>, though, is read after commit at replay time, so it is the current tag, not the first
    /// response's: only the status and the target are the stored result, not the tag.
    /// </para>
    /// </remarks>
    private static async Task<IResult> RejectAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        [FromHeader(Name = RequireIdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
        ISender sender,
        PostCommitETagReader etags,
        IOptions<RequestLimitsOptions> limits,
        HttpContext http,
        CancellationToken ct)
    {
        var raw = await ReadBodyAsync(http, checked((int)limits.Value.MaxRequestBodySize), ct).ConfigureAwait(false);
        if (raw is null)
        {
            return ProblemDetailsWriter.ToProblem([ProblemDetailsWriter.PayloadTooLargeError], http);
        }

        var body = ReadRejectBody(raw.Value, http.Request);
        if (body.IsError)
        {
            return ProblemDetailsWriter.ToProblem(body.Errors, http);
        }

        var result = await sender
            .Send(new RejectVideoCommand(id, body.Value.Reason, body.Value.Target, ifMatch, idempotencyKey), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return result.ToHttpResult(http);
        }

        var etag = await etags.EpisodeAsync(id, ct).ConfigureAwait(false);
        ErrorOr<EpisodeStateDto> rejected = new EpisodeStateDto(
            result.Value.EpisodeId,
            result.Value.Status,
            etag ?? string.Empty);

        // A re-render is accepted, not applied: the Worker starts it from the outbox event, so no render job id
        // exists to point a Location header at (WT3). The story target is a plain state change.
        return result.Value.Target is RejectTarget.ReRender
            ? rejected.ToAcceptedResult(http)
            : rejected.ToHttpResult(http);
    }

    /// <summary>
    /// Reads the request body under the host's cap (09 README BD12), the bounded-reader convention of the
    /// story-approval route: <see langword="null"/> means the body exceeded <paramref name="limit"/>; an empty
    /// body is an empty buffer and each route decides whether it is valid.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext http, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var declared = http.Request.ContentLength;
        if (declared > limit)
        {
            return null;
        }

        if (declared == 0
            || http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        var buffer = new byte[Math.Min(4 * 1_024, declared is { } length ? (int)length + 1 : limit + 1)];
        var total = 0;
        try
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, limit + 1L));
                }

                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.AsMemory(0, total);
                }

                total += read;
                if (total > limit)
                {
                    return null;
                }
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the optional approval body: empty means "no note"; otherwise a JSON object whose optional
    /// <c>note</c> is a string. Anything else — a non-object root, a non-string note, a non-JSON content type —
    /// is 400 <c>validation_failed</c> under the offending member.
    /// </summary>
    private static ErrorOr<ApproveVideoRequest> ReadApproveBody(ReadOnlyMemory<byte> raw, HttpRequest request)
    {
        if (raw.IsEmpty)
        {
            return new ApproveVideoRequest();
        }

        if (!request.HasJsonContentType())
        {
            return Errors.ValidationFailed("body", "The request body must be a JSON object.");
        }

        try
        {
            return JsonSerializer.Deserialize<ApproveVideoRequest>(raw.Span, BodyJson) is { } body
                ? body
                : Errors.ValidationFailed("body", "The request body must be a JSON object.");
        }
        catch (JsonException exception)
        {
            return Errors.ValidationFailed(FieldOf(exception.Path), "The request body is not a valid approval.");
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape cannot be transcoded. Do not expose body text through an exception.
            return Errors.ValidationFailed("body", "The request body is not a valid approval.");
        }
    }

    /// <summary>
    /// Reads the required reject body: a JSON object with a <c>reason</c> and a <c>target</c> whose snake_case
    /// value names a <see cref="RejectTarget"/> member.
    /// </summary>
    /// <remarks>
    /// The member a failing body names comes from the reader's path (<c>$.target</c>), so the problem's field
    /// points at the value the client must fix; anything else is reported under <c>body</c>. The discriminator is
    /// required, so a body that omits, nulls or numbers it is refused with <c>target</c> as the field instead of
    /// silently taking the <c>story</c> branch (D1); the reason's own 1–500 check stays in the command validator.
    /// </remarks>
    private static ErrorOr<RejectVideoRequest> ReadRejectBody(ReadOnlyMemory<byte> raw, HttpRequest request)
    {
        if (raw.IsEmpty)
        {
            return Errors.ValidationFailed("body", "The request body is required.");
        }

        if (!request.HasJsonContentType())
        {
            return Errors.ValidationFailed("body", "The request body must be a JSON object.");
        }

        try
        {
            return JsonSerializer.Deserialize<RejectVideoRequest>(raw.Span, BodyJson) is { } body
                ? body
                : Errors.ValidationFailed("body", "The request body must be a JSON object.");
        }
        catch (JsonException exception)
        {
            return Errors.ValidationFailed(
                OmitsTarget(exception.Path, raw) ? "target" : FieldOf(exception.Path),
                "The request body is not a valid rejection.");
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape cannot be transcoded. Do not expose body text through an exception.
            return Errors.ValidationFailed("body", "The request body is not a valid rejection.");
        }
    }

    /// <summary>
    /// Whether a refused reject body omitted its required <c>target</c>.
    /// </summary>
    /// <param name="path">The path of the refusal, <c>$.target</c> for a member the serializer reached.</param>
    /// <param name="raw">The body that was refused.</param>
    /// <returns><see langword="true"/> when the body omits the member.</returns>
    /// <remarks>
    /// The serializer reports a member it never saw at the body's own path, and reports every refusal it can
    /// locate at that member's path — so a root path leaves two possibilities: the omitted member, or a body that
    /// is not an object at all. A body that reads as a JSON object here is the first one, because the parser
    /// accepted it (a body it cannot read, such as truncated or over-deep JSON, is not an object).
    /// </remarks>
    private static bool OmitsTarget(string? path, ReadOnlyMemory<byte> raw)
    {
        if (path is not (null or "" or "$"))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind is JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The member a body error is reported under: the last path segment when it names one of this surface's
    /// members (<c>note</c>, <c>reason</c>, <c>target</c>), <c>body</c> otherwise.
    /// </summary>
    private static string FieldOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "body";
        }

        var separator = path.LastIndexOf('.');
        var member = separator >= 0 ? path[(separator + 1)..] : path;

        return member is "note" or "reason" or "target" ? member : "body";
    }
}
