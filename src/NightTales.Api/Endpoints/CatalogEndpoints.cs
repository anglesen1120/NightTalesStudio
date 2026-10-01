using System.Security.Cryptography;
using System.Text.Json;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NightTales.Api.Http;
using NightTales.Application.Features.Catalog.Dtos;
using NightTales.Application.Features.Catalog.Queries.ListStyleTemplates;
using NightTales.Application.Features.Catalog.Queries.ListVoices;

// The minimal-API JSON options of the host (Program.cs registers the snake_case enum converter on them);
// the alias keeps the name apart from Microsoft.AspNetCore.Mvc.JsonOptions, which is not the one used.
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The two catalog routes of 09.4.2 (09 README D14, plan §5): the selectable voices and the style
/// templates allowed for a topic, each with a cache header and a cheap ETag that makes a repeat load a
/// 304 (plan §4 UC6).
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are deliberately thin (09.1.2-T1): they bind the frozen query strings, send one message
/// through the D4 pipeline and turn the outcome into the documented response. What is theirs alone is
/// the response envelope, the <c>Cache-Control</c> header and the ETag/304 pair — none of which the
/// shared <c>ResultExtensions</c> helpers express (plan §10 open item 4), so this file computes them
/// instead of editing that shared file.
/// </para>
/// <para>
/// Neither route carries <c>AllowAnonymous</c>: every message is denied by default, so an anonymous
/// caller answers 401 <c>unauthorized</c> before a handler runs (09 README D4/D10) — the same rule the
/// series and episode routes follow, and the reason this file adds no <c>RequireAuthorization()</c> on
/// top of it.
/// </para>
/// <para>
/// The two sets are closed and bounded (04 D9 voices, 03 D2 seven templates), so both answers carry the
/// whole catalog as <c>{ "items": [ … ] }</c> — no <c>cursor</c>, no <c>limit</c> ([DECISION] plan §5).
/// </para>
/// </remarks>
public static class CatalogEndpoints
{
    /// <summary>The voices route (09 §4 09.4.2, 09 README D14).</summary>
    public const string VoicesRoute = "/api/v1/voices";

    /// <summary>The style-template route (09 §4 09.4.2, 09 README D14).</summary>
    public const string StyleTemplatesRoute = "/api/v1/style-templates";

    /// <summary>Maps the two catalog routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(VoicesRoute, ListVoicesAsync)
            .Produces<CatalogResponse<VoiceListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        routes.MapGet(StyleTemplatesRoute, ListStyleTemplatesAsync)
            .Produces<CatalogResponse<StyleTemplateListItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/v1/voices?language=</c>: 200 with <c>{ items }</c>, <c>ETag</c> and
    /// <c>Cache-Control</c>, 304 when the client's tag is still current, or 400
    /// <c>validation_failed</c> for an unknown language.
    /// </summary>
    /// <remarks>
    /// <c>language</c> is bound as a string on purpose: the wire value is the snake_case name
    /// (09 README D14), which the built-in enum binder would refuse, and an unknown name has to be a 400
    /// under field <c>language</c> rather than a silently dropped filter.
    /// </remarks>
    private static async Task<IResult> ListVoicesAsync(
        [FromQuery(Name = "language")] string? language,
        ISender sender,
        IOptions<HttpJsonOptions> jsonOptions,
        IOptions<CatalogOptions> catalogOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListVoicesQuery(language), ct).ConfigureAwait(false);

        return result.IsError
            ? ProblemDetailsWriter.ToProblem(result.Errors, http)
            : CatalogResult(
                result.Value,
                jsonOptions.Value.SerializerOptions,
                catalogOptions.Value.PublicCacheSeconds,
                http);
    }

    /// <summary>
    /// <c>GET /api/v1/style-templates?topic=</c>: the same answer shape as the voices route, with 400
    /// <c>validation_failed</c> for an unknown topic.
    /// </summary>
    private static async Task<IResult> ListStyleTemplatesAsync(
        [FromQuery(Name = "topic")] string? topic,
        ISender sender,
        IOptions<HttpJsonOptions> jsonOptions,
        IOptions<CatalogOptions> catalogOptions,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListStyleTemplatesQuery(topic), ct).ConfigureAwait(false);

        return result.IsError
            ? ProblemDetailsWriter.ToProblem(result.Errors, http)
            : CatalogResult(
                result.Value,
                jsonOptions.Value.SerializerOptions,
                catalogOptions.Value.PublicCacheSeconds,
                http);
    }

    /// <summary>
    /// The shared answer of both catalogs: the cache header, the ETag of the current items, and either
    /// the body or a 304.
    /// </summary>
    /// <remarks>
    /// The ETag is the first 16 hex characters of the SHA-256 of the serialized items (plan §Design), so
    /// any change to the catalog — a new voice, a de-activated template, a renamed row — changes it and
    /// the next revalidation answers 200. It is a strong validator, compared exactly as D7 requires:
    /// <c>*</c>, a list, a malformed value or an unquoted tag never matches, and the client simply gets
    /// the body again.
    /// </remarks>
    /// <typeparam name="T">The item type of the catalog.</typeparam>
    /// <param name="items">The items the handler produced, in their deterministic order.</param>
    /// <param name="jsonOptions">The host's HTTP JSON options, shared with the body so both agree.</param>
    /// <param name="cacheSeconds">The <c>max-age</c> of the answer (<c>Catalog:PublicCacheSeconds</c>).</param>
    /// <param name="http">The request whose <c>If-None-Match</c> decides 200 or 304.</param>
    /// <returns>The 200 with the envelope, or the 304.</returns>
    private static IResult CatalogResult<T>(
        IReadOnlyList<T> items,
        JsonSerializerOptions jsonOptions,
        int cacheSeconds,
        HttpContext http)
    {
        var etag = ComputeETag(items, jsonOptions);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = $"public, max-age={cacheSeconds}";

        if (MatchesIfNoneMatch(http.Request.Headers.IfNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Json(new CatalogResponse<T>(items), jsonOptions, statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// The route's ETag: the first 16 hex characters of the SHA-256 of the items as this route serializes
    /// them (plan §Design — <c>[INFERENCE]</c>, confirmed by the 304/200 tests).
    /// </summary>
    private static string ComputeETag<T>(IReadOnlyList<T> items, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(items, jsonOptions);
        var hash = SHA256.HashData(payload);

        return $"\"{Convert.ToHexStringLower(hash)[..16]}\"";
    }

    /// <summary>
    /// Whether the request already holds the current tag. The comparison is the exact-string, strong one
    /// of 09 README D7: no weak-tag equivalence (<c>W/"…"</c>), no prefix match, and <c>*</c> is not
    /// treated as "any" on a catalog route — a client with a stale tag gets the body again.
    /// </summary>
    private static bool MatchesIfNoneMatch(StringValues ifNoneMatch, string etag)
    {
        foreach (var candidate in ifNoneMatch)
        {
            if (string.Equals(candidate, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The <c>{ "items": [ … ] }</c> envelope of both catalog routes ([DECISION] plan §5): no <c>cursor</c>
/// and no <c>limit</c>, because the sets are closed and 10.4.2 needs the whole list in one call.
/// </summary>
/// <typeparam name="T">The item type of the catalog.</typeparam>
/// <param name="Items">The catalog rows, in their deterministic order.</param>
public sealed record CatalogResponse<T>(IReadOnlyList<T> Items);
