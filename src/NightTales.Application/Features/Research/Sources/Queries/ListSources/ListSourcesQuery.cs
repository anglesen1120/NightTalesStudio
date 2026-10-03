using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Research.Sources.Queries.ListSources;

/// <summary><c>GET /api/v1/episodes/{episodeId}/sources</c> (02.5.1 plan §4 UC3): the episode's sources and its limits.</summary>
/// <param name="EpisodeId">The route value.</param>
public sealed record ListSourcesQuery(Guid EpisodeId) : IQuery<ErrorOr<SourceListDto>>;

/// <summary>The list body: the sources, oldest first, and the limit block the UI shows.</summary>
/// <param name="Items">The sources.</param>
/// <param name="Limits">The caps and the user's current usage.</param>
public sealed record SourceListDto(IReadOnlyList<SourceListItemDto> Items, SourceLimitsDto Limits);

/// <summary>The caps, computed server-side so the UI never guesses.</summary>
/// <param name="MaxPerEpisode">Sources allowed on the episode.</param>
/// <param name="UrlSourcesLast24h">Url sources the user created in the rolling 24 h.</param>
/// <param name="MaxUrlSourcesPerDay">Url sources the user may create in the rolling 24 h.</param>
public sealed record SourceLimitsDto(int MaxPerEpisode, int UrlSourcesLast24h, int MaxUrlSourcesPerDay);
