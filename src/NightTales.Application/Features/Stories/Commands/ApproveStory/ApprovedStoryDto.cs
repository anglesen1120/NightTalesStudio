using NightTales.Application.Common;

namespace NightTales.Application.Features.Stories.Commands.ApproveStory;

/// <summary>The locked version, its story ETag and its approval timestamp.</summary>
/// <param name="EpisodeId">The approved episode.</param>
/// <param name="StoryVersionId">The version locked for rendering.</param>
/// <param name="Etag">The strong story ETag, including quotes.</param>
/// <param name="Status">The episode's wire status.</param>
/// <param name="ApprovedAt">When the approval was committed.</param>
public sealed record ApprovedStoryDto(
    Guid EpisodeId,
    Guid StoryVersionId,
    string Etag,
    string Status,
    DateTimeOffset ApprovedAt) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
