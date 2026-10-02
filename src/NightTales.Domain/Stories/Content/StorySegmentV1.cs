namespace NightTales.Domain.Stories.Content;

/// <summary>
/// One <c>story.v1</c> segment (02 README D3): the hook, one body paragraph or the ending. <c>hook</c>
/// and <c>ending</c> have no beat, so <see cref="Beat"/> is set on paragraphs only.
/// </summary>
/// <param name="Text">The prose; no markdown, brackets, list markers or URLs.</param>
/// <param name="Keywords">The image keywords, 1–2 per segment and English (03 FR-03-3).</param>
/// <param name="FactRefs">The fact ids this segment cites; empty when it cites none.</param>
/// <param name="Beat">The D3 beat of a paragraph; null on the hook and the ending.</param>
public sealed record StorySegmentV1(
    string Text,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> FactRefs,
    string? Beat = null);
