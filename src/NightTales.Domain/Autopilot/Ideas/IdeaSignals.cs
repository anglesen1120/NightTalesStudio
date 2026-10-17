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
    /// The measured inputs of a candidate whose judge call degraded (02.8.1 plan §4 U2, task Design
    /// "Novelty" degradation): the lexical score decides freshness, and the judge's absent fit and hook
    /// take the neutral values the plan prescribes.
    /// </summary>
    /// <remarks>
    /// A judge that fails — invalid output after its retry, a provider error, an outage — is not a reason
    /// to fail the run, so the batch keeps scoring on what it did measure. The neutral values are exactly
    /// the plan's: <c>JudgeScore = 0</c> so freshness becomes <c>1 − lexical</c>, <c>JudgeFit = 1.0</c> so
    /// an idea the judge never saw is not punished for fit, and <c>JudgeHookStrength = 0.5</c> so the hook
    /// component is neutral rather than rewarding or punishing an unmeasured hook. Stating them here keeps
    /// the scorer one expression and one source for the degradation.
    /// </remarks>
    /// <param name="lexicalScore">The lexical similarity to the corpus, 0…1 (0 when nothing overlapped).</param>
    /// <param name="varietyPenalty">The variety guard's penalty for this candidate, 0…1 (0 in 02.8.1-T2, where the guard is not wired).</param>
    /// <returns>The signals the scorer reads for a judge-degraded candidate.</returns>
    public static IdeaSignals LexicalOnly(decimal lexicalScore, decimal varietyPenalty) =>
        new(
            lexicalScore,
            JudgeScore: 0m,
            JudgeFit: 1m,
            JudgeHookStrength: 0.5m,
            VarietyPenalty: varietyPenalty);

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
