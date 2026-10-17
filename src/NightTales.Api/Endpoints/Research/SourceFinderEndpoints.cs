using Mediator;
using Microsoft.AspNetCore.Mvc;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Api.RateLimiting;
using NightTales.Application.Features.Research.Commands.AddSourceCandidate;
using NightTales.Application.Features.Research.Commands.DismissSourceCandidate;
using NightTales.Application.Features.Research.Commands.FindSources;
using NightTales.Application.Features.Research.Queries.ListSourceCandidates;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Api.Endpoints.Research;

/// <summary>
/// The source-finder routes of an episode (02.7 plan §5, 02.7.2-T2): request a search, list the candidates a
/// finished search proposed, add one through the normal source path, dismiss one.
/// </summary>
/// <remarks>
/// <para>
/// The routes live under the episode's source group, next to the 02.5.1 source routes, because a candidate is
/// only ever a proposal for that episode. They answer in the same conventions: thin handlers that bind and
/// send one message through the D4 pipeline, another tenant's episode a 404 from the tenant-filtered lookup,
/// and the enum wire values of the domain enums.
/// </para>
/// <para>
/// Only <c>POST …/find</c> carries the feature guard: it is the one route that spends platform money and
/// starts a run. A request whose flag is off answers exactly like a missing route (404
/// <c>feature_disabled</c>, 09 README BD13) before the <c>Idempotency-Key</c> check runs, because the guard is
/// attached first. The candidate routes keep working while the flag is off: they only read and mutate rows a
/// previous, flag-on request already wrote, and hiding them would strand those rows.
/// </para>
/// <para>
/// The HTTP route always requests a <see cref="GenerationTrigger.User"/> search: the body carries the optional
/// query and nothing else, so a client cannot claim to be the autopilot or the system (02.7 README D19). The
/// <c>Idempotency-Key</c> is required and the route carries the <c>paid</c> rate-limit policy, exactly as the
/// fact-sheet generation route does (09 README D6).
/// </para>
/// </remarks>
public static class SourceFinderEndpoints
{
    /// <summary>The feature flag that guards the search route (<c>source_finder</c>).</summary>
    public const string FeatureKey = "source_finder";

    /// <summary>The group's prefix, the episode's source group of 02.5.1.</summary>
    public const string GroupPrefix = SourceEndpoints.GroupPrefix;

    /// <summary>The search route, relative to <see cref="GroupPrefix"/>.</summary>
    public const string FindRoute = "/find";

    /// <summary>The candidate routes, relative to <see cref="GroupPrefix"/>.</summary>
    public const string CandidatesRoute = "/candidates";

    /// <summary>Maps the source-finder routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    public static IEndpointRouteBuilder MapSourceFinderEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var sources = routes.MapGroup(GroupPrefix);
        sources.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        EpisodeIdRouteParameter.Declare(
            sources.MapPost(FindRoute, FindAsync)
                .RequireFeature(FeatureKey)
                .RequireIdempotencyKey()
                .RequireRateLimiting(RateLimitPolicies.Paid)
                .Produces<FindSourcesAccepted>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status429TooManyRequests),
            "episodeId");

        EpisodeIdRouteParameter.Declare(
            sources.MapGet(CandidatesRoute, ListAsync)
                .Produces<SourceCandidateListDto>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound),
            "episodeId");

        EpisodeIdRouteParameter.Declare(
            sources.MapPost($"{CandidatesRoute}/{{candidateId:guid}}/add", AddAsync)
                .Produces<SourceDto>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status429TooManyRequests),
            "episodeId");

        EpisodeIdRouteParameter.Declare(
            sources.MapPost($"{CandidatesRoute}/{{candidateId:guid}}/dismiss", DismissAsync)
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict),
            "episodeId");

        return routes;
    }

    /// <summary>
    /// <c>POST …/find</c>: 202 with the queued run id and <c>Location: /api/v1/episodes/{id}</c>, or the
    /// documented 400 <c>idempotency_key_required</c> / <c>validation_failed</c>, 404 <c>feature_disabled</c> /
    /// <c>not_found</c>, 409 <c>find_sources_in_progress</c> / <c>episode_invalid_state</c>, 422
    /// <c>idempotency_key_reused</c>, 429 <c>spend_cap_reached</c>.
    /// </summary>
    private static async Task<IResult> FindAsync(
        Guid episodeId,
        [FromBody] FindSourcesRequest? request,
        [FromHeader(Name = RequireIdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
            .Send(
                new FindSourcesCommand(episodeId, request?.Query, GenerationTrigger.User, idempotencyKey ?? string.Empty),
                ct)
            .ConfigureAwait(false))
            .ToAcceptedResult(http, _ => $"{EpisodeEndpoints.GroupPrefix}/{episodeId}");

    /// <summary><c>GET …/candidates</c>: 200 with the episode's candidates, or 404.</summary>
    private static async Task<IResult> ListAsync(Guid episodeId, ISender sender, HttpContext http, CancellationToken ct)
        => (await sender.Send(new ListSourceCandidatesQuery(episodeId), ct).ConfigureAwait(false))
            .ToHttpResult(http);

    /// <summary>
    /// <c>POST …/candidates/{candidateId}/add</c>: 201 with the created source and its <c>Location</c>, or the
    /// 400 <c>source_url_invalid</c> / <c>source_domain_not_allowed</c>, 404, 409 <c>source_duplicate</c> /
    /// <c>source_limit_reached</c> / <c>source_candidate_not_proposed</c> / <c>episode_invalid_state</c>, 429
    /// <c>source_fetch_limit_reached</c> of the normal source path (02.5.1).
    /// </summary>
    private static async Task<IResult> AddAsync(
        Guid episodeId,
        Guid candidateId,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => await (await sender
                .Send(new AddSourceCandidateCommand(episodeId, candidateId), ct)
                .ConfigureAwait(false))
            .ToCreatedResult(
                http,
                created => $"/api/v1/episodes/{episodeId}/sources/{created.Id}",
                (_, _) => Task.FromResult<string?>(null))
            .ConfigureAwait(false);

    /// <summary>
    /// <c>POST …/candidates/{candidateId}/dismiss</c>: 204, or 404, or 409
    /// <c>source_candidate_not_proposed</c> (the candidate was already added). Dismissing a dismissed
    /// candidate is a 204 no-op.
    /// </summary>
    private static async Task<IResult> DismissAsync(
        Guid episodeId,
        Guid candidateId,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
        => (await sender
            .Send(new DismissSourceCandidateCommand(episodeId, candidateId), ct)
            .ConfigureAwait(false))
            .ToNoContentResult(http);
}

/// <summary>
/// The <c>POST …/find</c> body (02.7 plan §5): the optional free-text query and nothing else. The trigger is
/// never bound from the body — the route always requests a user-triggered search (D19).
/// </summary>
/// <param name="Query">What to search for; trimmed and capped at 300 characters by the command validator.</param>
public sealed record FindSourcesRequest(string? Query);
