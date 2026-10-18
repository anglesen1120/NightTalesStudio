using ErrorOr;
using Mediator;
using NightTales.Application.Features.Publishing.Dtos;

namespace NightTales.Application.Features.Publishing.Queries.GetEpisodePublications;

/// <summary>
/// <c>GET /api/v1/episodes/{id}/publications</c> (06.2.1-T1 task §Application Queries, 06 README D6): every
/// publication of one episode of the caller, newest first.
/// </summary>
/// <remarks>
/// No paging and no cursor: an episode has a handful of rows — one per platform post — and the client reads the
/// whole set to render the publishing panel of the episode page.
/// </remarks>
/// <param name="EpisodeId">The episode whose publications are read (<c>{id}</c>).</param>
public sealed record GetEpisodePublicationsQuery(Guid EpisodeId) : IQuery<ErrorOr<IReadOnlyList<PublicationDto>>>;
