using ErrorOr;
using Mediator;
using NightTales.Application.Features.Stories.ReadModels;

namespace NightTales.Application.Features.Stories.Queries.GetStoryVersion;

/// <summary>One immutable version of one tenant-visible episode.</summary>
/// <param name="EpisodeId">The episode whose story is read.</param>
/// <param name="VersionId">The version id, constrained to the episode.</param>
public sealed record GetStoryVersionQuery(Guid EpisodeId, Guid VersionId) : IQuery<ErrorOr<StoryVersionDetailDto>>;
