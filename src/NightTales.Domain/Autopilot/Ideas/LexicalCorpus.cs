using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// One entry of the novelty corpus a batch is measured against (02.8.1-T2, task Design "Novelty"): an
/// approved episode or a pending idea of the series.
/// </summary>
/// <remarks>
/// The entry carries its own text so the corpus is prepared once per batch and the caller never re-reads
/// the rows. <see cref="Kind"/> is the caller's own label for where the entry came from; the lexical pass
/// echoes it and never interprets it, because what the kind means is the corpus provider's business.
/// </remarks>
/// <param name="Id">The row's id; reported back as <see cref="LexicalMatch.OverlapId"/>.</param>
/// <param name="Kind">The caller's label of the entry's origin, for example <c>episode</c> or <c>pending_idea</c>.</param>
/// <param name="Text">The text the shingle pass reads; normalised when the corpus is built.</param>
public sealed record LexicalCorpusEntry(Guid Id, string Kind, string Text);

/// <summary>
/// The lexical pass' verdict for one candidate (02.8.1-T2, task Design "Novelty"): the highest Jaccard
/// similarity the candidate's shingles reached against any corpus entry, and which entry produced it.
/// </summary>
/// <remarks>
/// Nothing overlapping is not the same as overlapping a dissimilar entry: a score of zero carries no id,
/// because there is no entry to name. The distinction matters for the stored
/// <see cref="IdeaNoveltyLexical.MaxOverlapId"/>, which is the evidence a rejected candidate can be
/// explained with.
/// </remarks>
/// <param name="Score">The highest similarity, 0…1; 0 when nothing overlapped.</param>
/// <param name="OverlapId">The id of the entry that produced <paramref name="Score"/>, or <see langword="null"/>.</param>
/// <param name="OverlapKind">That entry's <see cref="LexicalCorpusEntry.Kind"/>, or <see langword="null"/>.</param>
public sealed record LexicalMatch(decimal Score, Guid? OverlapId, string? OverlapKind)
{
    /// <summary>The verdict of a pass that found nothing to overlap with.</summary>
    public static LexicalMatch None { get; } = new(0m, null, null);
}

/// <summary>
/// The lexical half of the novelty measurement (02.8.1-T2, task Design "Lexical"): the series' corpus for
/// one batch, its shingle sets prepared once, and the Jaccard score of a candidate against every entry.
/// </summary>
/// <remarks>
/// <para>
/// The shingle rule is 02.5.6-T1's, reused and never reimplemented (<c>TextNormalizer</c>,
/// <c>Shingler</c>, <c>ShingleSet</c>, <c>SimilarityOptions.NFor</c>: vi 10, en 8): a second normaliser
/// would make idea novelty disagree with source similarity. The only cost of a pass is hashing the
/// candidate — the corpus side is hashed once, in <see cref="Build"/> — so the whole pass is free (the
/// plan's "cost 0").
/// </para>
/// <para>
/// <see cref="ScoreCandidate"/> is deterministic: the maximum is taken with a strict comparison over the
/// corpus in the order it was built, so a tie between two entries always names the first one, and a
/// candidate whose text shingles to nothing (fewer than <c>n</c> tokens) scores zero and claims no
/// overlap rather than comparing empty sets to every entry.
/// </para>
/// </remarks>
public sealed class LexicalCorpus
{
    private readonly LexicalEntry[] _entries;
    private readonly Language _language;
    private readonly int _n;

    private LexicalCorpus(LexicalEntry[] entries, Language language, int n)
    {
        _entries = entries;
        _language = language;
        _n = n;
    }

    /// <summary>The number of entries the corpus was prepared from.</summary>
    public int Count => _entries.Length;

    /// <summary>Prepares the shingle set of every entry, once, for the language's window size.</summary>
    /// <param name="entries">The corpus entries, in the order the caller wants ties resolved.</param>
    /// <param name="language">The series' content language: <c>vi</c> shingles at 10, everything else at 8.</param>
    /// <returns>The prepared corpus; it does not observe later changes to <paramref name="entries"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or an entry of it is <see langword="null"/>.</exception>
    public static LexicalCorpus Build(IEnumerable<LexicalCorpusEntry> entries, Language language)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var n = SimilarityOptions.NFor(language);
        var prepared = new List<LexicalEntry>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            prepared.Add(new LexicalEntry(
                entry.Id,
                entry.Kind,
                ShingleSet.From(Shingler.Hash(TextNormalizer.Normalize(entry.Text, language), n))));
        }

        return new LexicalCorpus([.. prepared], language, n);
    }

    /// <summary>Scores one candidate text against every entry and reports the closest of them.</summary>
    /// <remarks>
    /// The text is normalised here, so the caller passes the raw candidate text (the pass applies the
    /// same normalisation to the corpus in <see cref="Build"/>). An entry whose own text is too short to
    /// shingle is scored as zero and can never become the maximum, which leaves its id unreported.
    /// </remarks>
    /// <param name="text">The candidate's shingle text, before normalisation.</param>
    /// <returns>The highest similarity and the entry that produced it, or <see cref="LexicalMatch.None"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public LexicalMatch ScoreCandidate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var candidate = ShingleSet.From(Shingler.Hash(TextNormalizer.Normalize(text, _language), _n));
        if (candidate.Count == 0)
        {
            // Fewer than n tokens: nothing to compare, and no overlap to claim (task "Lexical").
            return LexicalMatch.None;
        }

        var best = 0m;
        Guid? bestId = null;
        string? bestKind = null;
        foreach (var entry in _entries)
        {
            var score = Similarity(candidate, entry.Shingles);
            if (score > best)
            {
                // Strictly greater, so the first entry of a tie keeps the id.
                best = score;
                bestId = entry.Id;
                bestKind = entry.Kind;
            }
        }

        return new LexicalMatch(best, bestId, bestKind);
    }

    /// <summary>The Jaccard similarity of two shingle sets: <c>|A∩B| / |A∪B|</c>, 0 when both are empty.</summary>
    private static decimal Similarity(IReadOnlySet<ulong> left, IReadOnlySet<ulong> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0m;
        }

        var (smaller, larger) = left.Count <= right.Count ? (left, right) : (right, left);
        var shared = 0;
        foreach (var shingle in smaller)
        {
            if (larger.Contains(shingle))
            {
                shared++;
            }
        }

        // The union never needs a third set: |A∪B| = |A| + |B| − |A∩B|, counted once.
        return (decimal)shared / ((left.Count + right.Count) - shared);
    }

    /// <summary>One prepared entry: its identity as reported back and the shingle set built once.</summary>
    private sealed record LexicalEntry(Guid Id, string Kind, IReadOnlySet<ulong> Shingles);
}
