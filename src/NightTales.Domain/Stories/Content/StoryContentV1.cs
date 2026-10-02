namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The <c>story.v1</c> model output (02 README D3): the draft exactly as the writer returns it. The
/// persistence layer adds <c>schema_version</c> and <c>fact_sources</c> to the stored document, never to
/// this type, and the model never emits URLs or source ids.
/// </summary>
/// <param name="Title">The episode title (≤ 100 characters, no markup).</param>
/// <param name="Hook">The opening segment; the D3 opening 20 s.</param>
/// <param name="Paragraphs">The body paragraphs, each carrying its beat.</param>
/// <param name="Ending">The closing segment.</param>
/// <param name="Description">The upload description (≤ 1,000 characters).</param>
/// <param name="Tags">The upload tags (F36 limits; no URL, <c>&lt;</c>, <c>&gt;</c>, <c>,</c> or <c>#</c>).</param>
/// <param name="ContentWarnings">The D3 <see cref="StoryEnums.ContentWarnings"/> values.</param>
/// <param name="Structure">The D3 structure tags.</param>
public sealed record StoryContentV1(
    string Title,
    StorySegmentV1 Hook,
    IReadOnlyList<StorySegmentV1> Paragraphs,
    StorySegmentV1 Ending,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ContentWarnings,
    StoryStructureV1 Structure);
