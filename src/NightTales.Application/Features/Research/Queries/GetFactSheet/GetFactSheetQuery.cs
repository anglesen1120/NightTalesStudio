using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Research.Queries.GetFactSheet;

/// <summary>
/// <c>GET /api/v1/episodes/{id}/fact-sheet</c> (02.5.4-T1, plan §4.4): the episode's current version — its
/// facts and the sources they cite, resolved through the episode's <c>story_sources</c> — with the <c>fs-</c>
/// ETag a following edit or approve must send back.
/// </summary>
/// <param name="EpisodeId">The episode whose fact sheet is read.</param>
public sealed record GetFactSheetQuery(Guid EpisodeId) : IQuery<ErrorOr<FactSheetDto>>;
