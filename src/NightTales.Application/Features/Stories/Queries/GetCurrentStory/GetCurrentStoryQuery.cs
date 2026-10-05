using ErrorOr;
using Mediator;
using NightTales.Application.Features.Stories.ReadModels;

namespace NightTales.Application.Features.Stories.Queries.GetCurrentStory;

/// <summary>The tenant-visible episode current story, or null before its first version.</summary>
/// <param name="EpisodeId">The episode whose story is read.</param>
public sealed record GetCurrentStoryQuery(Guid EpisodeId) : IQuery<ErrorOr<CurrentStoryDto?>>;
