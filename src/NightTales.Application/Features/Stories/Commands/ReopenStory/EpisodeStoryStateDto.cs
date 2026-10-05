using NightTales.Application.Common;

namespace NightTales.Application.Features.Stories.Commands.ReopenStory;

/// <summary>The reopened episode and the current story's ETag, carried in both the body and HTTP header.</summary>
/// <param name="EpisodeId">The episode id.</param>
/// <param name="StoryVersionId">The current story version id.</param>
/// <param name="Etag">The current story version's sv ETag.</param>
/// <param name="Status">The episode status wire value.</param>
public sealed record EpisodeStoryStateDto(Guid EpisodeId, Guid StoryVersionId, string Etag, string Status) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
