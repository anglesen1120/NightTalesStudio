namespace NightTales.Application.Features.Research.Queries.ListSourceCandidates;

/// <summary>
/// The list body of <c>GET /api/v1/episodes/{episodeId}/sources/candidates</c> (02.7.2-T2 R9): the latest find run
/// first (<c>created_at</c> desc), the run's own order within it (<c>rank</c> asc).
/// </summary>
/// <param name="Items">The episode's candidates.</param>
public sealed record SourceCandidateListDto(IReadOnlyList<SourceCandidateDto> Items);
