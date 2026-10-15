using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Publishing.Queries.GetYouTubeEta;

/// <summary>Reads how many YouTube upload units remain on the current Pacific quota day, and when the next slot opens.</summary>
public sealed record GetYouTubeEtaQuery : IQuery<ErrorOr<YouTubeEtaDto>>;
