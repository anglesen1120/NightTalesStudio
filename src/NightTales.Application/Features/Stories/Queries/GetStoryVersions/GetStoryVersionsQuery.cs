using ErrorOr;
using Mediator;
using NightTales.Application.Features.Stories.ReadModels;

namespace NightTales.Application.Features.Stories.Queries.GetStoryVersions;

/// <summary>A bounded newest-first history of one tenant-visible episode.</summary>
/// <param name="EpisodeId">The episode whose story is read.</param>
public sealed record GetStoryVersionsQuery(Guid EpisodeId) : IQuery<ErrorOr<IReadOnlyList<StoryVersionSummaryDto>>>;
