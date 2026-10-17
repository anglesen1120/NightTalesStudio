namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The scoring weights of the pick (02.8.1 plan §3.4), bound from
/// <c>Autopilot:Ideas:Weights</c> by the Infrastructure layer.
/// </summary>
/// <remarks>
/// The four weights are the plan's first guesses and are expected to be calibrated against real runs
/// (plan §10 item 8), so nothing here is a constant the scorer may hard-code. A weight of zero disables
/// its component; the sum need not be one, because the scorer (<c>IdeaScorer</c>, 02.8.1-T2) clamps the weighted sum to
/// 0…1 whatever the weights are.
/// </remarks>
/// <param name="Novelty">The weight of freshness, <c>1 − max(lexical, judge)</c>; 0.45.</param>
/// <param name="Fit">The weight of the judge's fit score; 0.30.</param>
/// <param name="Hook">The weight of the judge's hook strength; 0.25.</param>
/// <param name="Variety">The weight of the variety penalty, subtracted; 0.35.</param>
public sealed record IdeaWeights(
    decimal Novelty = 0.45m,
    decimal Fit = 0.30m,
    decimal Hook = 0.25m,
    decimal Variety = 0.35m);
