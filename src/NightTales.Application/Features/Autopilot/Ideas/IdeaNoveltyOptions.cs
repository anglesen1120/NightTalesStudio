using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The novelty thresholds and the corpus bound of the idea step (02.8.1-T2, task Design "Novelty"):
/// <c>Autopilot:Ideas:Novelty</c>. The section is bound once and registered as a singleton value, so the
/// Infrastructure corpus provider and the Application checker and planner read the same instance and cannot
/// disagree on the keys.
/// </summary>
public sealed class IdeaNoveltyOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "Autopilot:Ideas:Novelty";

    /// <summary>The shipped lexical ceiling: a candidate this similar to the corpus is rejected.</summary>
    /// <remarks>[WARN] A first guess (F32 marks the threshold uncalibrated): calibrate against the first ~50 real runs.</remarks>
    public const decimal DefaultLexicalMax = 0.35m;

    /// <summary>The shipped judge ceiling: a candidate the judge scores above it is rejected.</summary>
    /// <remarks>[WARN] A first guess, like <see cref="DefaultLexicalMax"/>.</remarks>
    public const decimal DefaultJudgeMax = 0.70m;

    /// <summary>How many approved episodes of the series the corpus carries.</summary>
    public const int DefaultCorpusEpisodes = 100;

    /// <summary>
    /// The lexical rejection threshold, 0…1: a candidate whose closest corpus overlap is <b>strictly greater</b>
    /// than this is rejected with <see cref="IdeaRejectionReason.NoveltyLexical"/>.
    /// </summary>
    public decimal LexicalMax { get; set; } = DefaultLexicalMax;

    /// <summary>
    /// The judge rejection threshold, 0…1: a candidate the judge scores <b>strictly greater</b> than this is
    /// rejected with <see cref="IdeaRejectionReason.NoveltyJudge"/>.
    /// </summary>
    public decimal JudgeMax { get; set; } = DefaultJudgeMax;

    /// <summary>The size of the episode corpus (<c>CorpusEpisodes</c>); never below one.</summary>
    public int CorpusEpisodes { get; set; } = DefaultCorpusEpisodes;

    /// <summary>The effective lexical ceiling: a mis-configured value outside 0…1 falls back to the shipped default.</summary>
    public decimal EffectiveLexicalMax =>
        LexicalMax is >= 0m and <= 1m ? LexicalMax : DefaultLexicalMax;

    /// <summary>The effective judge ceiling: a mis-configured value outside 0…1 falls back to the shipped default.</summary>
    public decimal EffectiveJudgeMax => JudgeMax is >= 0m and <= 1m ? JudgeMax : DefaultJudgeMax;
}
