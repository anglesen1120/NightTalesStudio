namespace NightTales.Domain.Stories.Similarity;

/// <summary>Pure lexical overlap over distinct story shingles, streaming source windows.</summary>
public static class SimilarityScorer
{
    public const string SourcesCorpus = "sources";
    public const string PastEpisodesCorpus = "past_episodes";

    /// <summary>Scores the union of separately shingled story segments against the source corpus.</summary>
    public static SimilarityResult Score(StoryText story, IReadOnlyList<CorpusText> corpus, SimilarityOptions opt)
    {
        var storyHashes = new HashSet<ulong>();
        var segments = new List<(string Address, HashSet<ulong> Hashes)>(story.Segments.Count);
        foreach (var segment in story.Segments)
        {
            var hashes = new HashSet<ulong>(Shingler.Windows(TextNormalizer.Normalize(segment.Text, opt.Language), opt.N));
            storyHashes.UnionWith(hashes);
            segments.Add((segment.Address, hashes));
        }

        var matched = Match(storyHashes, corpus, opt);
        var overlaps = new List<SegmentOverlap>();
        foreach (var (address, hashes) in segments)
        {
            var count = 0;
            foreach (var hash in hashes)
            {
                if (matched.Contains(hash))
                {
                    count++;
                }
            }

            if (count > 0)
            {
                overlaps.Add(new SegmentOverlap(address, (double)count / hashes.Count));
            }
        }

        overlaps.Sort(static (left, right) =>
        {
            var shareOrder = right.Share.CompareTo(left.Share);
            return shareOrder != 0 ? shareOrder : StringComparer.Ordinal.Compare(left.Segment, right.Segment);
        });
        if (overlaps.Count > 10)
        {
            overlaps.RemoveRange(10, overlaps.Count - 10);
        }

        return new SimilarityResult(storyHashes.Count == 0 ? 0 : (double)matched.Count / storyHashes.Count,
            opt.N, SourcesCorpus, overlaps);
    }

    /// <summary>Requires enough normalized story tokens and at least one nonempty normalized source.</summary>
    public static bool IsScorable(StoryText story, IReadOnlyList<CorpusText> corpus, SimilarityOptions opt)
    {
        long tokens = 0;
        foreach (var segment in story.Segments)
        {
            var normalized = TextNormalizer.Normalize(segment.Text, opt.Language);
            if (normalized.Length > 0)
            {
                tokens++;
                foreach (var c in normalized)
                {
                    if (c == ' ')
                    {
                        tokens++;
                    }
                }
            }
        }

        if (tokens < opt.MinTokens)
        {
            return false;
        }

        foreach (var text in corpus)
        {
            if (TextNormalizer.HasTokenContent(text.Text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the share of statement shingles found in its cited sources, or zero if too short.</summary>
    public static double ScoreFact(string statement, IReadOnlyList<CorpusText> sources, SimilarityOptions opt)
    {
        var hashes = new HashSet<ulong>(Shingler.Windows(TextNormalizer.Normalize(statement, opt.Language), opt.N));
        return hashes.Count == 0 ? 0 : (double)Match(hashes, sources, opt).Count / hashes.Count;
    }

    private static HashSet<ulong> Match(HashSet<ulong> storyHashes, IReadOnlyList<CorpusText> corpus, SimilarityOptions opt)
    {
        var matched = new HashSet<ulong>();
        if (storyHashes.Count == 0)
        {
            return matched;
        }

        foreach (var text in corpus)
        {
            foreach (var hash in Shingler.Windows(TextNormalizer.Normalize(text.Text, opt.Language), opt.N))
            {
                if (storyHashes.Contains(hash))
                {
                    matched.Add(hash);
                    if (matched.Count == storyHashes.Count)
                    {
                        return matched;
                    }
                }
            }
        }

        return matched;
    }
}
