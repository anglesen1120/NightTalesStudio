using System.Collections;
using System.Text;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// One piece of generated story text under its D22 address: the unit the real-world detector (02.8.2-T2) and the
/// content gate read, so every hit and every flag can name the place the owner has to look at (10.5.10).
/// </summary>
/// <param name="Id">
/// The D22 address (<see cref="SegmentAddress"/>): <c>title</c>, <c>hook</c>, <c>p:&lt;0-based paragraph
/// index&gt;</c>, <c>ending</c> or <c>meta</c>.
/// </param>
/// <param name="Text">The text exactly as stored, leading and trailing whitespace included; never blank.</param>
public sealed record StoryTextSegment(string Id, string Text);

/// <summary>
/// The generated text of one story version in D22 addressing and story order (02.8.2-T2): <c>title</c>,
/// <c>hook</c>, the body paragraphs as <c>p:0</c>…<c>p:n-1</c>, <c>ending</c> and the document-level <c>meta</c>
/// text. Immutable, and a null or blank text contributes no segment, so a detector can never report a hit on
/// empty prose.
/// </summary>
/// <remarks>
/// The paragraph index is the one the stored content uses, exactly as <see cref="SegmentAddress.Paragraph"/> and
/// <c>audit_report.local.issues[].segment</c> address it (02 README D22, <c>p:&lt;0-based index&gt;</c>; written by
/// the checker from the paragraph's own position in <c>story.v1.paragraphs</c>). A blank paragraph is therefore
/// skipped <em>without</em> renumbering the paragraphs after it: <c>p:3</c> stays the fourth stored paragraph even
/// when the third is empty. Only generated content is ever held here — the owner's brief, notes and fact
/// statements never enter (F31, task §"Failure handling").
/// </remarks>
public sealed class StoryTextSegments : IReadOnlyList<StoryTextSegment>
{
    private static readonly StoryTextSegment[] None = [];

    private readonly StoryTextSegment[] _segments;

    private StoryTextSegments(StoryTextSegment[] segments) => _segments = segments;

    /// <summary>The story with no text at all: nothing for a detector to read.</summary>
    public static StoryTextSegments Empty { get; } = new(None);

    /// <summary>Assembles the segments from the stored parts, skipping every null or blank one.</summary>
    /// <param name="title">The version's title, or null.</param>
    /// <param name="hook">The hook segment's text, or null.</param>
    /// <param name="paragraphs">The body paragraph texts in D22 order; the index of each is its position here.</param>
    /// <param name="ending">The ending segment's text, or null.</param>
    /// <param name="meta">The document-level metadata text (description and tags), or null.</param>
    /// <returns>The populated segments, in story order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paragraphs"/> is null.</exception>
    public static StoryTextSegments Create(
        string? title,
        string? hook,
        IReadOnlyList<string> paragraphs,
        string? ending,
        string? meta)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);

        var segments = new List<StoryTextSegment>(paragraphs.Count + 4);
        Add(segments, SegmentAddress.Title, title);
        Add(segments, SegmentAddress.Hook, hook);

        for (var index = 0; index < paragraphs.Count; index++)
        {
            // The index is the stored one: a blank paragraph is skipped, never renumbered (D22).
            Add(segments, SegmentAddress.Paragraph(index), paragraphs[index]);
        }

        Add(segments, SegmentAddress.Ending, ending);
        Add(segments, SegmentAddress.Meta, meta);

        return segments.Count == 0 ? Empty : new StoryTextSegments([.. segments]);
    }

    /// <summary>
    /// Assembles the segments from a stored <c>story.v1</c> document, the shape <c>story_versions.content</c>
    /// carries. <c>meta</c> is the description followed by the tags, one per line. The detector collapses every
    /// whitespace run (a line break included) to one space, so the join behaves like a space: a phrase that runs
    /// from the end of the description into the first tag is matched on purpose (fail closed). The similarity
    /// projection deliberately excludes the metadata, this one does not, because a description that says "based
    /// on real events" is exactly the F31 signal.
    /// </summary>
    /// <param name="content">The stored content, or null.</param>
    /// <returns>The populated segments, or <see cref="Empty"/> when there is no content.</returns>
    public static StoryTextSegments From(StoryContentV1? content)
    {
        if (content is null)
        {
            return Empty;
        }

        return Create(content.Title, content.Hook?.Text, ParagraphTexts(content), content.Ending?.Text, Meta(content));
    }

    /// <summary>How many non-blank segments the story has.</summary>
    public int Count => _segments.Length;

    /// <summary>The segment at <paramref name="index"/>, in story order.</summary>
    /// <param name="index">The position in this list, not the D22 paragraph index.</param>
    /// <returns>The segment.</returns>
    public StoryTextSegment this[int index] => _segments[index];

    /// <summary>Enumerates the segments in story order.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<StoryTextSegment> GetEnumerator() =>
        ((IEnumerable<StoryTextSegment>)_segments).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void Add(List<StoryTextSegment> segments, string address, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            segments.Add(new StoryTextSegment(address, text));
        }
    }

    private static string[] ParagraphTexts(StoryContentV1 content)
    {
        if (content.Paragraphs is not { } stored)
        {
            return [];
        }

        var texts = new string[stored.Count];
        for (var index = 0; index < texts.Length; index++)
        {
            texts[index] = stored[index]?.Text ?? string.Empty;
        }

        return texts;
    }

    private static string Meta(StoryContentV1 content)
    {
        var builder = new StringBuilder();

        Append(builder, content.Description);

        if (content.Tags is { Count: > 0 } tags)
        {
            foreach (var tag in tags)
            {
                Append(builder, tag);
            }
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(line);
    }
}
