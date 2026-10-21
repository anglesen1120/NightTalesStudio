#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// The variety guard's calibration knobs (02.8.4-T1, config section <c>Autopilot:Variety</c>): how far the
/// rules look back and which thresholds decide a rejection, a penalty or a steering value.
/// </summary>
/// <remarks>
/// <para>
/// The values are the first guesses of plan §10 item 10 — <see cref="WindowK"/> = 10,
/// <see cref="TemplateWindowK"/> = 5, <see cref="TagShareMax"/> = 0.40, <see cref="RotationMinCount"/> = 3 —
/// and the 02.8.2-T3 dry run is the calibration vehicle, so no rule hard-codes them.
/// </para>
/// <para>
/// The record is bound from configuration and registered as a singleton; the guard never reads
/// <c>IConfiguration</c> itself, which is what keeps it pure and unit-testable.
/// </para>
/// </remarks>
/// <param name="WindowK">The number of most recent episodes R1, R2 and R4 look back over.</param>
/// <param name="TemplateWindowK">The shorter window R3 (the template repeat) looks back over.</param>
/// <param name="TagShareMax">The share of the window one tag value may reach before it is forbidden (R2).</param>
/// <param name="RotationMinCount">A value seen fewer times than this in the window is a rotation candidate (R4).</param>
/// <param name="Enabled">
/// When <see langword="false"/>, <see cref="VarietyGuard.Evaluate"/> rejects nothing and penalises nothing (the
/// remaining steering is kept); the rollback switch of the task's failure-handling table.
/// </param>
public sealed record VarietyOptions(
    int WindowK,
    int TemplateWindowK,
    decimal TagShareMax,
    int RotationMinCount,
    bool Enabled)
{
    /// <summary>The shipped defaults: a 10/5 window, a 40 % tag-share cap, a rotation floor of 3, enabled.</summary>
    public static VarietyOptions Default { get; } = new(10, 5, 0.40m, 3, true);
}
