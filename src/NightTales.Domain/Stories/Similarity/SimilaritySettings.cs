namespace NightTales.Domain.Stories.Similarity;

/// <summary>Plain similarity configuration, independent of options binding or dependency injection.</summary>
public sealed class SimilaritySettings
{
    public double WarnAbove { get; set; } = 0.05;
    public double BlockAbove { get; set; } = 0.15;
    public int MinTokens { get; set; } = 50;

    /// <summary>
    /// The <c>Similarity:FactOverlapBlock</c> threshold of the fact-sheet verbatim-copy rule (02.5.3-T2 rule 4,
    /// F18): a fact whose statement scores above it against its cited sources is dropped. It defaults to the
    /// 02.5.6 <see cref="BlockAbove"/> default (<c>0.15</c>), because the two thresholds answer the same
    /// question at different granularities (a story, a fact statement).
    /// </summary>
    public double FactOverlapBlock { get; set; } = DefaultFactOverlapBlock;

    /// <summary>The frozen default of <see cref="FactOverlapBlock"/>: the 02.5.6 <c>BlockAbove</c> default.</summary>
    public const double DefaultFactOverlapBlock = 0.15;
    public Dictionary<string, int> N { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vi"] = 10,
        ["en"] = 8,
    };
    public SimilarityCorpusSettings Corpus { get; set; } = new();
    public SimilarityPerfSettings Perf { get; set; } = new();
}

/// <summary>Limits on the corpus selected and read by the application.</summary>
public sealed class SimilarityCorpusSettings
{
    public int MaxSourceTexts { get; set; } = 10;
    public int MaxSourceTextChars { get; set; } = 300000;
    public int MaxPastEpisodes { get; set; } = 50;
}

/// <summary>The performance budget enforced by the performance acceptance test.</summary>
public sealed class SimilarityPerfSettings
{
    public int BudgetMs { get; set; } = 500;
}
