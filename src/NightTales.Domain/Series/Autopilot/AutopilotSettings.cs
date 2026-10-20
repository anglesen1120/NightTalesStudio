using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The typed form of <c>series.autopilot_settings</c> (plan §3.1): everything an autopilot run needs to plan a
/// slot and produce the episode without the owner.
/// </summary>
/// <remarks>
/// <para>
/// The record is a carrier, not a guard: <see cref="AutopilotSettingsValidator.Validate"/> owns the ranges and
/// answers <c>autopilot_settings_invalid</c> with one field-level error per broken rule, and
/// <see cref="AutopilotSettingsValidator.Serialize"/> writes the same snake_case document back. Nothing here
/// normalises or defaults a value, because a default would hide a document the owner never wrote — a missing
/// settings document is a series that does not publish under autopilot, not one that publishes with the
/// plan's example values.
/// </para>
/// <para>
/// <b>Equality.</b> Record equality compares the two collections (<see cref="AutopilotCadence.DaysOfWeek"/> and
/// <see cref="BannedThemes"/>) by reference, so a round trip is proven on the serialised document
/// (<c>Serialize(Validate(x).Value)</c>) and never on <c>==</c>. <see cref="Privacy"/> reuses
/// <see cref="PrivacyStatus"/>, the enum the authorization and the publication already share (decision 1), so
/// the autopilot cannot send a privacy the publication route would refuse.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">The document's schema version; exactly <c>1</c> today.</param>
/// <param name="Cadence">The weekdays, local publish time and time zone the slots are computed from.</param>
/// <param name="DailyCap">Publications per channel per local day, 1..5 (the planner applies it across series).</param>
/// <param name="BufferDays">Ready-buffer depth <em>and</em> planning horizon, 1..7 (plan §3.4).</param>
/// <param name="Privacy">The privacy a publication of this series is created and uploaded with.</param>
/// <param name="VetoWindowHours">How long the owner has to veto before the upload, 2..48.</param>
/// <param name="PreAuditMode">What happens to a publication while the channel is unaudited.</param>
/// <param name="PreAuditPauseAfter">The N of <see cref="PreAuditMode.PauseAfterNPrivate"/>, 1..20.</param>
/// <param name="GateThresholdsRef">The gate-profile name 02.8.2 reads; <c>default</c> is the shipped profile.</param>
/// <param name="Tone">The tone brief 02.8.1 feeds the story prompt with, at most 500 characters.</param>
/// <param name="BannedThemes">The themes the story prompt must avoid; at most 20 themes of 100 characters each.</param>
/// <param name="RenderLeadMinutes">
/// How long the render is expected to take; <see langword="null"/> falls back to
/// <c>Autopilot:RenderEstimate:Minutes</c> (plan §3.4).
/// </param>
/// <param name="StoryLeadMinutes">
/// How long the story generation is expected to take; <see langword="null"/> falls back to
/// <c>Autopilot:StoryEstimate:Minutes</c> (plan §3.4).
/// </param>
public sealed record AutopilotSettings(
    int SchemaVersion,
    AutopilotCadence Cadence,
    int DailyCap,
    int BufferDays,
    PrivacyStatus Privacy,
    int VetoWindowHours,
    PreAuditMode PreAuditMode,
    int PreAuditPauseAfter,
    string GateThresholdsRef,
    string Tone,
    IReadOnlyList<string> BannedThemes,
    int? RenderLeadMinutes,
    int? StoryLeadMinutes);
