namespace NightTales.Domain.Stories.Similarity;

/// <summary>Plain similarity configuration, independent of options binding or dependency injection.</summary>
public sealed class SimilaritySettings
{
    public double WarnAbove { get; set; } = 0.05;
    public double BlockAbove { get; set; } = 0.15;
    public int MinTokens { get; set; } = 50;
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
