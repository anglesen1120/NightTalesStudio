using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Application.Abstractions.Stories;

/// <summary>The persisted approved text and ordered scene keywords handed to the render pipeline.</summary>
/// <param name="StoryVersionId">The exact version selected by the caller.</param>
/// <param name="Body">The stored D3-derived video script.</param>
/// <param name="Keywords">The stored keywords in hook, paragraph and ending order.</param>
/// <param name="Title">The version's title.</param>
/// <param name="Language">The series language wire name.</param>
/// <param name="Tier">The episode's length tier.</param>
/// <param name="Segments">Segment addresses and keywords in story order.</param>
public sealed record StoryRenderInput(
    Guid StoryVersionId,
    string Body,
    IReadOnlyList<string> Keywords,
    string Title,
    string Language,
    LengthTier Tier,
    IReadOnlyList<SegmentKeywords> Segments);
