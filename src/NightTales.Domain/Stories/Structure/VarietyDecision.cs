#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// The variety guard's verdict on one idea candidate (02.8.4-T1): the hard-block reason, the R2 penalty the
/// scorer subtracts, and the soft violations that widen the batch's forbidden set.
/// </summary>
/// <remarks>
/// <para>
/// A candidate with a <see cref="RejectReason"/> is rejected before scoring (02.8.1-T2 plan §3.4); it still
/// carries its soft violations so the run log can explain the penalty it would have paid. Only the
/// <see cref="NightTales.Domain.Autopilot.Ideas.IdeaRejectionReason.VarietyTripleRepeat"/> and
/// <see cref="NightTales.Domain.Autopilot.Ideas.IdeaRejectionReason.VarietyTemplateRepeat"/> reasons are legal,
/// and R3 wins when both hit.
/// </para>
/// <para>
/// <see cref="SoftViolations"/> holds one <c>"&lt;dimension&gt;:&lt;value&gt;"</c> entry per R2 hit, in the fixed
/// dimension order <c>sub_genre</c>, <c>setting</c>, <c>opening</c>, <c>twist_type</c>.
/// </para>
/// </remarks>
/// <param name="Penalty">The soft penalty: <c>0</c>, <c>0.5</c> for one violation, <c>0.75</c> for two or more.</param>
/// <param name="RejectReason">The hard-block reason, or <see langword="null"/> when the candidate survives.</param>
/// <param name="SoftViolations">The R2 violations as <c>"&lt;dimension&gt;:&lt;value&gt;"</c>; empty when none.</param>
public sealed record VarietyDecision(decimal Penalty, string? RejectReason, IReadOnlyList<string> SoftViolations)
{
    /// <summary>No rejection and no penalty — the value a disabled guard, a cold start or a clean candidate gets.</summary>
    public static VarietyDecision None { get; } = new(0m, null, []);
}
