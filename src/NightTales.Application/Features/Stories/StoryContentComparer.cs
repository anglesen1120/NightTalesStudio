using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories;

/// <summary>
/// The no-op rule of the edit command (02.3.1-T1 [DECISION]): two drafts are the same story when their canonical
/// forms are equal, so a client that re-serialises an unchanged story creates no noise version. Canonical means
/// NFC, <c>\r\n</c> and <c>\r</c> as <c>\n</c>, and surrounding whitespace trimmed, on every string; the order of
/// keywords, fact refs, tags and warnings is meaningful and kept.
/// </summary>
public static class StoryContentComparer
{
    /// <summary>Whether <paramref name="left"/> and <paramref name="right"/> are the same story after canonicalisation.</summary>
    /// <param name="left">One draft.</param>
    /// <param name="right">The other draft.</param>
    /// <returns><see langword="true"/> when title, every segment, description, tags, warnings and structure are equal.</returns>
    public static bool CanonicalEquals(StoryContentV1 left, StoryContentV1 right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return Same(left.Title, right.Title)
            && Same(left.Description, right.Description)
            && SameSegment(left.Hook, right.Hook)
            && SameSegment(left.Ending, right.Ending)
            && SameSegments(left.Paragraphs, right.Paragraphs)
            && SameList(left.Tags, right.Tags)
            && SameList(left.ContentWarnings, right.ContentWarnings)
            && SameStructure(left.Structure, right.Structure);
    }

    private static string Canon(string? text) =>
        StoryText.Normalize(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static bool Same(string? left, string? right) => string.Equals(Canon(left), Canon(right), StringComparison.Ordinal);

    private static bool SameList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        left ??= [];
        right ??= [];

        return left.Count == right.Count && left.Zip(right).All(pair => Same(pair.First, pair.Second));
    }

    private static bool SameSegment(StorySegmentV1? left, StorySegmentV1? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return Same(left.Text, right.Text)
            && Same(left.Beat, right.Beat)
            && SameList(left.Keywords, right.Keywords)
            && SameList(left.FactRefs, right.FactRefs);
    }

    private static bool SameSegments(IReadOnlyList<StorySegmentV1>? left, IReadOnlyList<StorySegmentV1>? right)
    {
        left ??= [];
        right ??= [];

        return left.Count == right.Count && left.Zip(right).All(pair => SameSegment(pair.First, pair.Second));
    }

    private static bool SameStructure(StoryStructureV1? left, StoryStructureV1? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return Same(left.SubGenre, right.SubGenre)
            && Same(left.Setting, right.Setting)
            && Same(left.Opening, right.Opening)
            && Same(left.TwistType, right.TwistType)
            && Same(left.NarratorMode, right.NarratorMode);
    }
}
