namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The story gate's verdict (02.8.2 plan §3.4, the <c>story</c> block of <c>gate_results</c>).
/// </summary>
/// <param name="Pass">True when every check passed. A true-crime story never passes.</param>
/// <param name="Hold">
/// True only for the true-crime hold (plan §3.1 check 15), so T4 and the run log can tell "held for the owner
/// by design" from "failed a check".
/// </param>
/// <param name="Attempt">The story-gate attempt of the run (1 or 2; D17 allows two generations per run).</param>
/// <param name="StoryVersionId">The evaluated story version.</param>
/// <param name="Checks">The verdict lines, in plan §3.1 order.</param>
/// <param name="Failures">The ids of the failing checks, in the same order.</param>
public sealed record StoryGateResult(
    bool Pass,
    bool Hold,
    int Attempt,
    Guid StoryVersionId,
    IReadOnlyList<GateCheck> Checks,
    IReadOnlyList<string> Failures);
