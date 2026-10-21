using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Commands.ApproveStory;
using NightTales.Application.Features.Stories.Commands.EditStory;
using NightTales.Application.Features.Stories.Commands.GenerateStory;
using NightTales.Application.Features.Stories.ReadModels;
using NightTales.Application.Features.Stories.Queries.GetStoryVersion;
using NightTales.Application.Features.Stories.Queries.GetStoryVersions;
using NightTales.Application.Features.Stories.Commands.ReopenStory;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The story routes (02 README D9, 02.2.2 plan §5.1, 02.3.1 plan §Route, 02.3.2-T1, 02.3.4-T2): generation,
/// full edits, approval, reopening and read-only version history. Other story operations belong to their own tasks.
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

    /// <summary>The read-only version history route (02.3.4-T2).</summary>
    public const string VersionsRoute = "/api/v1/episodes/{id:guid}/story/versions";

    /// <summary>The read-only version detail route (02 README CR-12).</summary>
    public const string VersionRoute = "/api/v1/episodes/{id:guid}/story/versions/{versionId:guid}";

    /// <summary>Locks the reviewed story version (02.3.2-T1).</summary>
    public const string ApproveRoute = StoryRoute + "/approve";

    /// <summary>Unlocks the approved story version (02.3.2-T1).</summary>
    public const string ReopenRoute = StoryRoute + "/reopen";

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
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
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
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        EpisodeIdRouteParameter.Declare(
            routes.MapGet(VersionsRoute, GetVersionsAsync)
                .Produces<IReadOnlyList<StoryVersionSummaryDto>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        EpisodeIdRouteParameter.Declare(
            routes.MapGet(VersionRoute, GetVersionAsync)
                .Produces<StoryVersionDetailDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy));

        // These commands validate ownership and state before If-Match (02.3.2-T1 fixed order).
        // Document the required header without the PUT route's early RequireIfMatchFilter.
        EpisodeIdRouteParameter.Declare(
            DocumentRequiredIfMatch(routes.MapPost(ApproveRoute, ApproveAsync)
                // The bounded reader below owns content-type validation (400), not routing's 415 matcher.
                .Accepts<ApproveStoryRequest>(isOptional: true, "*/*")
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    var content = operation.RequestBody!.Content!;
                    content["application/json"] = content["*/*"];
                    content.Remove("*/*");
                    return Task.CompletedTask;
                })
                .Produces<ApprovedStoryDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy)));

        EpisodeIdRouteParameter.Declare(
            DocumentRequiredIfMatch(routes.MapPost(ReopenRoute, ReopenAsync)
                .Produces<EpisodeStoryStateDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status428PreconditionRequired)
                .RequireCors(SecurityHeadersExtensions.SpaCorsPolicy)));

        return routes;
    }

    /// <summary>Reads summary columns only; a missing or foreign episode answers the same 404.</summary>
    private static async Task<IResult> GetVersionsAsync(
        Guid id,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetStoryVersionsQuery(id), ct).ConfigureAwait(false);
        if (!result.IsError)
        {
            http.Response.Headers.Vary = "Authorization";
            http.Response.Headers.CacheControl = "no-cache";
        }

        return result.ToHttpResult(http);
    }

    /// <summary>Reads one version; the shared result helper emits its <c>IHasETag</c> story tag.</summary>
    private static async Task<IResult> GetVersionAsync(
        Guid id,
        Guid versionId,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetStoryVersionQuery(id, versionId), ct).ConfigureAwait(false);
        if (!result.IsError)
        {
            http.Response.Headers.Vary = "Authorization";
            http.Response.Headers.CacheControl = "no-cache";
        }

        return result.ToHttpResult(http);
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

    /// <summary>The optional approval body; the caller cannot bind an approval source.</summary>
    public sealed record ApproveStoryRequest(AttestationInput? Attestation = null);

    private static RouteHandlerBuilder DocumentRequiredIfMatch(RouteHandlerBuilder route) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Parameters is not null)
            {
                foreach (var parameter in operation.Parameters)
                {
                    if (parameter is OpenApiParameter { In: ParameterLocation.Header } header
                        && string.Equals(header.Name, RequireIfMatchFilter.HeaderName, StringComparison.OrdinalIgnoreCase))
                    {
                        header.Required = true;
                    }
                }
            }

            return Task.CompletedTask;
        });

    private static async Task<IResult> ApproveAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        IOptions<RequestLimitsOptions> limits,
        HttpContext http,
        CancellationToken ct)
    {
        var raw = await ReadApprovalBodyAsync(http, checked((int)limits.Value.MaxRequestBodySize), ct).ConfigureAwait(false);
        if (raw is null)
        {
            return ProblemDetailsWriter.ToProblem(
                [ProblemDetailsWriter.PayloadTooLargeError],
                http);
        }

        // The command checks ownership before reporting body errors, so foreign/unknown episodes stay 404.
        var bodyInvalid = !TryReadAttestation(raw.Value, http.Request, out var attestation);

        return (await sender
            .Send(new ApproveStoryCommand(id, ifMatch ?? string.Empty, attestation, ApprovalSource.User)
            {
                BodyInvalid = bodyInvalid,
            }, ct)
            .ConfigureAwait(false))
            .ToHttpResult(http);
    }

    private static async Task<IResult> ReopenAsync(
        Guid id,
        [FromHeader(Name = RequireIfMatchFilter.HeaderName)] string? ifMatch,
        ISender sender,
        HttpContext http,
        CancellationToken ct) =>
        (await sender.Send(new ReopenStoryCommand(id, ifMatch ?? string.Empty), ct).ConfigureAwait(false))
        .ToHttpResult(http);

    // Same bounded reading convention as the fetcher endpoints; no body is a valid fiction approval.
    private static async Task<ReadOnlyMemory<byte>?> ReadApprovalBodyAsync(HttpContext http, int limit, CancellationToken ct)
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

    private static bool TryReadAttestation(ReadOnlyMemory<byte> raw, HttpRequest request, out AttestationInput? attestation)
    {
        attestation = null;
        if (raw.IsEmpty)
        {
            return true;
        }

        if (!request.HasJsonContentType())
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!body.TryGetProperty("attestation", out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("accepted", out var accepted)
                || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !value.TryGetProperty("textVersion", out var textVersion)
                || textVersion.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            // Shape only: topic-specific acceptance and text-version checks belong to the approval gate.
            attestation = new AttestationInput(accepted.GetBoolean(), textVersion.GetString()!);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape cannot be transcoded. Do not expose body text through an exception.
            return false;
        }
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
