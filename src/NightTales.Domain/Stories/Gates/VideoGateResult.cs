namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The video gate's verdict (02.8.2 plan §3.4, the <c>video</c> block of <c>gate_results</c>). A failure leaves
/// the episode in <c>video_review</c>; there is no hold here, the hold belongs to the story gate.
/// </summary>
/// <param name="Pass">True when every check passed.</param>
/// <param name="RenderJobId">The finished render job the verdict belongs to.</param>
/// <param name="AssetId">The stored asset the verdict checked.</param>
/// <param name="Checks">The verdict lines, in plan §3.2 order.</param>
/// <param name="Failures">The ids of the failing checks, in the same order.</param>
public sealed record VideoGateResult(
    bool Pass,
    Guid RenderJobId,
    Guid AssetId,
    IReadOnlyList<GateCheck> Checks,
    IReadOnlyList<string> Failures);
