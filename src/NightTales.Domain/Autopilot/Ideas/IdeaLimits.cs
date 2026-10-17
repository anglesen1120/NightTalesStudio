namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The per-field limits <see cref="IdeaValidator"/> enforces (02.8.1 plan §3.2), bound from
/// <c>Autopilot:Ideas:Limits</c> by the Infrastructure layer.
/// </summary>
/// <remarks>
/// <para>
/// The four character caps are the <c>varchar</c> widths of <c>episode_ideas</c> (<c>title</c> 100,
/// <c>logline</c> 300, <c>hook</c> 300, <c>setting</c> 120), so a candidate a run persists can never
/// exceed its column, and the EF configuration reads the widths from here instead of restating them.
/// The beat bounds are the strict schema's <c>beats</c> <c>minItems</c>/<c>maxItems</c> (3–8).
/// </para>
/// <para>
/// The defaults are the plan's numbers, so a series that never tunes the section behaves exactly as
/// specified. A configured value is not re-validated by the options binder, which is why
/// <see cref="IdeaValidator"/> reports malformed limits as one field error instead of walking candidates
/// with them.
/// </para>
/// </remarks>
/// <param name="TitleChars">The longest <c>title</c>; 100.</param>
/// <param name="LoglineChars">The longest <c>logline</c>; 300.</param>
/// <param name="HookChars">The longest <c>hook</c>; 300.</param>
/// <param name="SettingChars">The longest <c>setting</c>; 120.</param>
/// <param name="MinBeats">The fewest beats of one idea; 3.</param>
/// <param name="MaxBeats">The most beats of one idea; 8.</param>
public sealed record IdeaLimits(
    int TitleChars = 100,
    int LoglineChars = 300,
    int HookChars = 300,
    int SettingChars = 120,
    int MinBeats = 3,
    int MaxBeats = 8)
{
    /// <summary>Whether every limit is positive and the beat band is not inverted; a bound is unusable otherwise.</summary>
    public bool IsValid =>
        TitleChars > 0
        && LoglineChars > 0
        && HookChars > 0
        && SettingChars > 0
        && MinBeats > 0
        && MaxBeats >= MinBeats;
}
