namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The measured inputs of one candidate's score (02.8.1 plan §3.4): the two novelty scores, the judge's fit
/// and hook components and the variety penalty. 02.8.1-T2 fills it from the lexical pass, the one judge call
/// and 02.8.4-T1's guard.
/// </summary>
/// <remarks>
/// The record is pure data: it carries no decision, so the caller can store the components next to the score
/// (the calibration histograms) without re-deriving them.
/// </remarks>
/// <param name="LexicalScore">The lexical similarity to the corpus, 0…1.</param>
/// <param name="JudgeScore">The judge's similarity score, 0…1 (0 when the judge was skipped).</param>
/// <param name="JudgeFit">The judge's fit score, 0…1 (1 is the neutral value the scorer uses on degradation).</param>
/// <param name="JudgeHookStrength">The judge's hook strength, 0…1 (0.5 is the neutral value on degradation).</param>
/// <param name="VarietyPenalty">The variety guard's penalty: 0 clean, 0.5 soft cap hit; 1 blocks before scoring.</param>
public sealed record IdeaSignals(
    decimal LexicalScore,
    decimal JudgeScore,
    decimal JudgeFit,
    decimal JudgeHookStrength,
    decimal VarietyPenalty)
{
    /// <summary>
    /// The clamp the scorer (<c>IdeaScorer</c>, 02.8.1-T2) applies to its weighted sum: <paramref name="value"/> limited to
    /// 0…1.
    /// </summary>
    /// <remarks>
    /// The weights are configurable and an over-weighting configuration can push the sum out of range, so
    /// the score column's domain (0…1, <c>numeric(5,4)</c>) is restored here rather than trusted from the
    /// configuration.
    /// </remarks>
    /// <param name="value">The weighted sum.</param>
    /// <returns><paramref name="value"/> limited to 0…1.</returns>
    public static decimal Clamp01(decimal value)
    {
        if (value < 0m)
        {
            return 0m;
        }

        return value > 1m ? 1m : value;
    }
}
