using System.Globalization;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The segment-drift check of a single revision pass (02.6.2-T1, D21 step 5): compares the original draft
/// with the revision and reports the segments that changed although the review did not ask for them. Pure
/// and deterministic; it reuses <see cref="TextNormalizer"/> (D21) for the token-set similarity and reads
/// no story text beyond it. It fails closed: a missing paragraph list, a missing segment or a missing beat
/// on either side is drift, never "no drift". The result is advisory only.
/// </summary>
public static class SegmentDriftDetector
{
    /// <summary>
    /// Runs the drift check.
    /// </summary>
    /// <remarks>
    /// The task's declared signature is <c>Detect(StoryContentV1 original, StoryContentV1 revised,
    /// IReadOnlyCollection&lt;string&gt; addressedSegments, double minSimilarity)</c>. The check also needs the
    /// local checker's blocking rule ids of both drafts to find a finding the revision introduced — those are
    /// not carried by the review text — so <paramref name="originalBlockingRules"/> and
    /// <paramref name="revisedBlockingRules"/> are appended, and <paramref name="language"/> drives
    /// <see cref="TextNormalizer"/>.
    /// </remarks>
    /// <param name="original">The draft the revision was asked to change.</param>
    /// <param name="revised">The revision output.</param>
    /// <param name="addressedSegments">The D22 addresses the review asked to change; those may change freely. A paragraph address is compared canonically, so <c>p:01</c> and <c>p:1</c> name the same segment.</param>
    /// <param name="minSimilarity">The token-set similarity an untouched segment keeps; a value outside <c>(0, 1]</c> is clamped to <c>1</c> so an invalid threshold fails closed (D-config bounds it to <c>(0, 1]</c>).</param>
    /// <param name="originalBlockingRules">The local checker's blocking rule ids of the original draft.</param>
    /// <param name="revisedBlockingRules">The local checker's blocking rule ids of the revised draft.</param>
    /// <param name="language">The content language, for <see cref="TextNormalizer"/>.</param>
    /// <returns>The drift of the revision; a clean revision yields no count change, no changed segment and no added finding.</returns>
    public static RevisionDrift Detect(
        StoryContentV1 original,
        StoryContentV1 revised,
        IReadOnlyCollection<string> addressedSegments,
        double minSimilarity,
        IReadOnlyCollection<string> originalBlockingRules,
        IReadOnlyCollection<string> revisedBlockingRules,
        Language language)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(revised);
        ArgumentNullException.ThrowIfNull(addressedSegments);
        ArgumentNullException.ThrowIfNull(originalBlockingRules);
        ArgumentNullException.ThrowIfNull(revisedBlockingRules);

        var threshold = double.IsFinite(minSimilarity) && minSimilarity is > 0d and <= 1d ? minSimilarity : 1d;

        // The review's addresses are compared in canonical form: ReviewResultValidator accepts `p:01` while this
        // detector's own addresses are `p:1`, so a padded index would otherwise read as an untouched segment.
        var addressed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addressedSegments)
        {
            addressed.Add(CanonicalAddress(address));
        }

        var before = Segments(original);
        var after = Segments(revised);

        var changed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Mark(string address)
        {
            if (seen.Add(address))
            {
                changed.Add(address);
            }
        }

        // Fail closed: a null paragraph list loses every paragraph, so it is always drift, even when both sides are null.
        var countChanged = original.Paragraphs is null || revised.Paragraphs is null || before.Count != after.Count;

        var pairs = Math.Max(before.Count, after.Count);
        for (var index = 0; index < pairs; index++)
        {
            var hasBefore = index < before.Count;
            var hasAfter = index < after.Count;

            if (!hasBefore || !hasAfter)
            {
                countChanged = true;
                Mark((hasBefore ? before[index] : after[index]).Address);
                continue;
            }

            var left = before[index];
            var right = after[index];

            // An addressed segment may change however it likes; the review asked for it.
            if (addressed.Contains(left.Address))
            {
                continue;
            }

            // A missing segment, missing text or a changed (or lost) beat is drift; otherwise the token-set similarity decides.
            if (left.Text is null || right.Text is null
                || left.Missing || right.Missing
                || !string.Equals(left.Beat, right.Beat, StringComparison.Ordinal)
                || Similarity(left.Text, right.Text, language) < threshold)
            {
                Mark(left.Address);
            }
        }

        return new RevisionDrift(countChanged, changed, AddedFindings(originalBlockingRules, revisedBlockingRules));
    }

    private static List<SegmentView> Segments(StoryContentV1 story)
    {
        var segments = new List<SegmentView>(2 + (story.Paragraphs?.Count ?? 0))
        {
            View(SegmentAddress.Hook, story.Hook),
        };

        if (story.Paragraphs is { } paragraphs)
        {
            for (var index = 0; index < paragraphs.Count; index++)
            {
                segments.Add(View(SegmentAddress.Paragraph(index), paragraphs[index]));
            }
        }

        segments.Add(View(SegmentAddress.Ending, story.Ending));

        return segments;
    }

    /// <summary>
    /// The D22 paragraph address of <paramref name="address"/> with a canonical index: the review validator accepts
    /// <c>p:01</c> while <see cref="SegmentAddress.Paragraph"/> emits <c>p:1</c>, and a padded index would otherwise be
    /// compared against the canonical one and read as a segment the revision was not asked to change. Every other
    /// address — and a zero-padded index that is not a plain non-negative integer — is returned unchanged.
    /// </summary>
    /// <param name="address">An address a review issue declared.</param>
    /// <returns>The canonical D22 address.</returns>
    private static string CanonicalAddress(string address) =>
        address.StartsWith("p:", StringComparison.Ordinal)
        && int.TryParse(address.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? SegmentAddress.Paragraph(index)
            : address;

    private static SegmentView View(string address, StorySegmentV1? segment) =>
        segment is null
            ? new SegmentView(address, Text: null, Beat: null, Missing: true)
            : new SegmentView(address, segment.Text, segment.Beat, Missing: string.IsNullOrEmpty(segment.Text));

    private static double Similarity(string left, string right, Language language)
    {
        var leftTokens = Tokens(left, language);
        var rightTokens = Tokens(right, language);

        // Fail closed: an empty side has no shared content, so it never counts as "unchanged".
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0d;
        }

        var shared = 0;
        foreach (var token in leftTokens)
        {
            if (rightTokens.Contains(token))
            {
                shared++;
            }
        }

        return (double)shared / (leftTokens.Count + rightTokens.Count - shared);
    }

    private static HashSet<string> Tokens(string text, Language language)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in TextNormalizer.Normalize(text, language).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static List<string> AddedFindings(IReadOnlyCollection<string> original, IReadOnlyCollection<string> revised)
    {
        var prior = new HashSet<string>(original, StringComparer.Ordinal);
        var added = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in revised)
        {
            if (rule is { Length: > 0 } && !prior.Contains(rule))
            {
                added.Add(rule);
            }
        }

        return [.. added];
    }

    private readonly record struct SegmentView(string Address, string? Text, string? Beat, bool Missing);
}
