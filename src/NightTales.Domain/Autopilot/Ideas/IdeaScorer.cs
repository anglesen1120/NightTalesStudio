namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The pick of 02.8.1 plan §3.4: the weighted score of one candidate, and which eligible candidate wins a
/// batch. Pure and deterministic, because the run log, the calibration histograms and the stored
/// <see cref="EpisodeIdea.Score"/> all have to agree on one number and one winner.
/// </summary>
/// <remarks>
/// The score is the plan's formula over one <see cref="IdeaSignals"/> and the configured
/// <see cref="IdeaWeights"/>:
/// <c>clamp01( w.Novelty × (1 − max(lexical, judge)) + w.Fit × fit + w.Hook × hook − w.Variety × penalty )</c>.
/// A degraded judge call is not a special case here: <see cref="IdeaSignals.LexicalOnly"/> states the
/// neutral values (judge 0, fit 1.0, hook 0.5) the plan prescribes, so the scorer stays one expression.
/// </remarks>
public static class IdeaScorer
{
    /// <summary>The weighted score of one candidate, clamped to the <c>numeric(5,4)</c> column's 0…1.</summary>
    /// <remarks>
    /// The weights are configuration, so the sum may leave 0…1; <see cref="IdeaSignals.Clamp01"/> restores
    /// the domain of the column instead of trusting the configuration (see <see cref="IdeaWeights"/>).
    /// </remarks>
    /// <param name="signals">The measured inputs of the candidate.</param>
    /// <param name="weights">The configured component weights.</param>
    /// <returns>The candidate's score, 0…1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signals"/> or <paramref name="weights"/> is <see langword="null"/>.</exception>
    public static decimal Score(IdeaSignals signals, IdeaWeights weights)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(weights);

        var freshness = 1m - Math.Max(signals.LexicalScore, signals.JudgeScore);
        var weighted = (weights.Novelty * freshness)
            + (weights.Fit * signals.JudgeFit)
            + (weights.Hook * signals.JudgeHookStrength)
            - (weights.Variety * signals.VarietyPenalty);

        return IdeaSignals.Clamp01(weighted);
    }

    /// <summary>
    /// Which candidate of a batch wins: the highest score, ties broken by the lowest index.
    /// </summary>
    /// <remarks>
    /// The index is the candidate's position in the batch the caller scored, and the list's entries are
    /// <see langword="null"/> for candidates that are not eligible (rejected before scoring, or dropped by
    /// the validator), so those can never win. <see langword="null"/> means no candidate was eligible at
    /// all — the caller answers <c>IdeaErrors.NoIdea</c> instead of picking one.
    /// </remarks>
    /// <param name="scoresOfEligible">The scores of the batch, indexed by the candidate's batch position.</param>
    /// <returns>The winning index, or <see langword="null"/> when no entry held a score.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scoresOfEligible"/> is <see langword="null"/>.</exception>
    public static int? PickIndex(IReadOnlyList<decimal?> scoresOfEligible)
    {
        ArgumentNullException.ThrowIfNull(scoresOfEligible);

        int? winner = null;
        var best = 0m;
        for (var index = 0; index < scoresOfEligible.Count; index++)
        {
            if (scoresOfEligible[index] is not { } score)
            {
                continue;
            }

            if (winner is null || score > best)
            {
                // Strictly greater, so the lowest index of a tie wins.
                winner = index;
                best = score;
            }
        }

        return winner;
    }
}
