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
/// <param name="JudgeFit">The judge's fit score, 0…1 (1 when the whole batch degraded, 0.5 when only this candidate was left unjudged).</param>
/// <param name="JudgeHookStrength">The judge's hook strength, 0…1 (0.5 when the batch degraded or this candidate was left unjudged).</param>
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
    /// A judge that fails — invalid output after its retry, a provider error, an outage, a data object it
    /// refused — is not a reason to fail the run, so the batch keeps scoring on what it did measure. This is
    /// the whole batch's degradation, where no candidate has a judge reading at all. The neutral values are
    /// exactly the plan's: <c>JudgeScore = 0</c> so freshness becomes <c>1 − lexical</c>, <c>JudgeFit = 1.0</c>
    /// so the shared loss punishes nobody, and <c>JudgeHookStrength = 0.5</c> so the hook component is neutral
    /// rather than rewarding or punishing an unmeasured hook. Stating them here keeps the scorer one
    /// expression and one source for the degradation.
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
    /// The measured inputs of one candidate of a batch the judge <b>did</b> answer, for which its answer carried
    /// no usable row: the lexical score decides freshness and the judge's absent components take the neutral
    /// values — except fit, which is neutral at <c>0.5</c> here.
    /// </summary>
    /// <remarks>
    /// The distinction from <see cref="LexicalOnly"/> is what keeps a missing row from paying: fit carries the
    /// second-largest weight of the score (<c>Autopilot:Ideas:Scoring:Weights</c>), so a candidate the batch's
    /// judge never scored must not be handed the generous <c>1.0</c> the whole batch's degradation uses — that
    /// would let an unmeasured candidate outrank candidates the judge did measure, on the very component it
    /// never answered about. The whole batch degrades uniformly and the ranking inside it is unaffected; a
    /// mixed batch is ranked honestly. <c>JudgeScore = 0</c> and <c>JudgeHookStrength = 0.5</c> are the same
    /// neutrals in both cases: an absent similarity proves nothing about freshness, and the hook component is
    /// neutral either way (<c>IdeaSignals</c>, 02.8.1 plan §3.4).
    /// </remarks>
    /// <param name="lexicalScore">The lexical similarity to the corpus, 0…1 (0 when nothing overlapped).</param>
    /// <param name="varietyPenalty">The variety guard's penalty for this candidate, 0…1 (0 in 02.8.1-T2, where the guard is not wired).</param>
    /// <returns>The signals the scorer reads for a candidate no judge row mentioned.</returns>
    public static IdeaSignals Unjudged(decimal lexicalScore, decimal varietyPenalty) =>
        new(
            lexicalScore,
            JudgeScore: 0m,
            JudgeFit: 0.5m,
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
