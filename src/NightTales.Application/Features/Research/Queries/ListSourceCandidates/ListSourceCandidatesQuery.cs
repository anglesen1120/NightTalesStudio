using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Research.Queries.ListSourceCandidates;

/// <summary>
/// <c>GET /api/v1/episodes/{episodeId}/sources/candidates</c> (02.7.2 T2, plan §5): the episode's proposed, added and
/// dismissed source candidates.
/// </summary>
/// <param name="EpisodeId">The route value; the episode must belong to the caller.</param>
public sealed record ListSourceCandidatesQuery(Guid EpisodeId) : IQuery<ErrorOr<SourceCandidateListDto>>;
