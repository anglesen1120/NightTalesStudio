using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// <c>GET /api/v1/episodes/{id}</c> (09.5.3 plan §4 UC3, §5): the whole episode page in one call — the
/// status, the actions the domain allows next, the latest render job, the final video and the
/// publications placeholder — or a 404.
/// </summary>
/// <remarks>
/// Renamed from 09.5.1-T1's <c>GetEpisodeQuery</c> when the route grew the blocks (one route, one
/// handler, one DTO — never a second query handler for the same resource). Another tenant's id answers
/// the same 404 as a missing one and as a cancelled one (09 README D10, F14): the read path hides the
/// row, so the three cases are indistinguishable on purpose.
/// </remarks>
/// <param name="Id">The episode to read.</param>
public sealed record GetEpisodeByIdQuery(Guid Id) : IQuery<ErrorOr<EpisodeDetailDto>>;
