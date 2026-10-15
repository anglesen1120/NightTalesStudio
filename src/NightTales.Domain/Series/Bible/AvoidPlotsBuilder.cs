namespace NightTales.Domain.Series.Bible;

/// <summary>
/// Derives the writer's <c>avoid_plots</c> list from the episode summaries of a bounded bible snapshot (02.6.3 plan
/// §4 U4): the first sentence of each summary, so the writer learns what has already happened without the full plot.
/// </summary>
public static class AvoidPlotsBuilder
{
    /// <summary>The characters that end the first sentence of an approved summary, including the Vietnamese ellipsis.</summary>
    private static readonly char[] SentenceEnders = ['.', '!', '?', '…'];

    /// <summary>
    /// Builds the avoid-plots entries: the first sentence of each summary, in input order (newest first), deduplicated
    /// by <see cref="BibleOpNormalizer.Normalize"/> and cut to <paramref name="maxCharsPerEntry"/> on a word boundary.
    /// A title-only summary (no summary text) contributes nothing.
    /// </summary>
    /// <param name="summaries">The episode summaries of the bounded snapshot, newest first.</param>
    /// <param name="max">The most entries returned (<c>Bible:AvoidPlots:Max</c>).</param>
    /// <param name="maxCharsPerEntry">The length each entry is cut to (<c>Bible:AvoidPlots:MaxCharsPerEntry</c>).</param>
    /// <returns>At most <paramref name="max"/> distinct entries.</returns>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<BibleEpisodeSummarySnapshot> summaries,
        int max = 25,
        int maxCharsPerEntry = 120)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var entries = new List<string>();
        if (max <= 0 || maxCharsPerEntry <= 0)
        {
            return entries;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            if (entries.Count >= max)
            {
                break;
            }

            var sentence = FirstSentence(summary.Summary);
            if (sentence.Length == 0)
            {
                continue;
            }

            var entry = BibleOpNormalizer.Truncate(sentence, maxCharsPerEntry);
            var key = BibleOpNormalizer.Normalize(entry);
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>The summary up to and including its first sentence ender; the whole trimmed text when there is none.</summary>
    private static string FirstSentence(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var text = summary.Trim();
        var end = text.IndexOfAny(SentenceEnders);
        return end < 0 ? text : text[..(end + 1)];
    }
}
