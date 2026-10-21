using Mediator;
using NightTales.Api.Contracts.Publishing;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Application.Features.Publishing.Queries.GetEpisodePublications;

namespace NightTales.Api.Endpoints.Publishing;

/// <summary>
/// The publication routes of 06.2.1-T1 (06 README D6/D17): <c>POST /api/v1/episodes/{id}/publications</c>
/// creates the episode's publication in <c>awaiting_consent</c>, and <c>GET</c> lists the episode's own rows.
/// </summary>
/// <remarks>
/// <para>
/// The file is deliberately not <c>PublishingEndpoints.cs</c>: that name is the channel routes of 06.1.2
/// (<c>/api/v1/channels</c>). Both routes here hang under the episode resource instead, so they share
/// <see cref="EpisodeEndpoints.GroupPrefix"/> and the episode-id declaration that keeps the request log honest.
/// </para>
/// <para>
/// Both routes are thin (09.1.2-T1): bind, send one message through the D4 pipeline, answer. The metadata rules,
/// the pre-audit privacy override, the veto window, the audio attribution and the audit/outbox rows belong to the
/// command handler; the <c>Location</c>, the <c>ETag</c> and the <c>items</c> envelope belong here, because a
/// handler has no <c>HttpResponse</c> (09 README D14).
/// </para>
/// <para>
/// Both routes are behind <c>RequireFeature("youtube_publish")</c> (06 README D9): an authenticated caller with the
/// flag off answers 404 <c>feature_disabled</c>, and the filter is attached first so it answers before the
/// handler runs. An anonymous caller is 401 from <c>RequireAuthorization()</c>, which is middleware and runs
/// before any endpoint filter. Another tenant's episode — or an unknown id — is 404 <c>not_found</c> from the
/// tenant-filtered lookup, never 403 (09 README D10).
/// </para>
/// <para>
/// Neither route carries <c>RequireIdempotencyKey()</c>: §5.4's rule is for paid or platform-spending POSTs, and
/// this create spends nothing and calls nothing outside the database (the upload is 06.2.3's job), exactly like
/// the video-review routes of 09.7.1-T1. Neither carries a rate-limit policy for the same reason, and the create
/// needs no <c>If-Match</c> because it creates a new row rather than replacing one — the duplicate-active create
/// is the partial unique index's answer (409 <c>publication_invalid_state</c>), not a precondition on a tag.
/// </para>
/// </remarks>
public static class EpisodePublicationEndpoints
{
    /// <summary>The group's prefix; the two publication routes hang under it.</summary>
    public const string GroupPrefix = EpisodeEndpoints.GroupPrefix;

    /// <summary>The route, relative to <see cref="GroupPrefix"/>: the episode's publications collection.</summary>
    public const string Route = "/{id:guid}/publications";

    /// <summary>
    /// The feature flag that gates every YouTube publishing route (06 README D9); the same key the sibling
    /// publishing routes carry, declared once in <see cref="YouTubeEtaEndpoints"/>.
    /// </summary>
    public const string FeatureKey = YouTubeEtaEndpoints.FeatureKey;

    /// <summary>Maps the publication routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapEpisodePublicationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var publications = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy.
        publications.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        EpisodeIdRouteParameter.Declare(
            publications.MapPost(Route, CreateAsync)
                .RequireFeature(FeatureKey)
                .RequireAuthorization()
                .Produces<PublicationDto>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict));

        EpisodeIdRouteParameter.Declare(
            publications.MapGet(Route, ListAsync)
                .RequireFeature(FeatureKey)
                .RequireAuthorization()
                .Produces<PublicationListResponse>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status404NotFound));

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/episodes/{id}/publications</c>: 201 with the committed publication, its
    /// <c>Location</c> (the list this route also serves) and its post-commit <c>ETag</c>
    /// (<c>"pu-&lt;xmin&gt;"</c>), or the documented 400/401/404/409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A publication has no read-by-id route (D6), so <c>Location</c> points at the episode's publication list,
    /// where the created row is the newest one.
    /// </para>
    /// <para>
    /// The <c>ETag</c> is read after the unit of work committed (D15, 06 CR-2): <c>xmin</c> is assigned by
    /// PostgreSQL at commit, so the value the command could return is already stale. The tag is what every later
    /// consent, cancel or PATCH of this publication must echo as <c>If-Match</c>;
    /// <see cref="PostCommitETagReader.PublicationAsync"/> reads it through the tenant-filtered read model, so it
    /// can only ever see this caller's own row.
    /// </para>
    /// <para>
    /// <c>autopilotAuthorizationId</c> is not a body member (C1): the public route creates manual publications
    /// only, and 02.8.3 calls the same command in-process for its autopilot rows.
    /// </para>
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        Guid id,
        CreatePublicationRequest request,
        ISender sender,
        PostCommitETagReader etags,
        HttpContext http,
        CancellationToken ct)
    {
        var command = new CreatePublicationCommand(
            id,
            request.ChannelConnectionId,
            request.Title,
            request.Description,
            // YouTube accepts a video with no tags, so an omitted or null member is the empty list, not a 400.
            request.Tags ?? [],
            request.Privacy,
            request.PublishAt,
            AutopilotAuthorizationId: null);

        var result = await sender.Send(command, ct).ConfigureAwait(false);

        return await result
            .ToCreatedResult(
                http,
                _ => $"{GroupPrefix}/{id}/publications",
                (created, token) => etags.PublicationAsync(created.Id, token))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/v1/episodes/{id}/publications</c>: 200 with <c>{ items: [...] }</c> — the episode's own
    /// publications, newest first — or the documented 401/404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envelope is built here, not in the Application layer (09 README D6): the query answers the DTO list and
    /// the <c>items</c> member is the wire convention every list route shares.
    /// </para>
    /// <para>
    /// No ETag, no <c>If-Match</c>, no paging (D6/D15): the collection is read-only here, and an episode has a
    /// handful of publications. The xmin each row carries is the tag of the row's own PATCH route of 06.2.2.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ListAsync(Guid id, ISender sender, HttpContext http, CancellationToken ct)
    {
        var result = await sender
            .Send(new GetEpisodePublicationsQuery(id), ct)
            .ConfigureAwait(false);

        return result.IsError
            ? result.ToHttpResult(http)
            : Results.Json(new PublicationListResponse(result.Value), statusCode: StatusCodes.Status200OK);
    }
}
