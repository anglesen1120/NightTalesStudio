namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// When a series publishes under autopilot (plan §3.1 <c>autopilot_settings.cadence</c>): the weekdays, the
/// local publish time and the IANA time zone the two are read in.
/// </summary>
/// <remarks>
/// <para>
/// The cadence is a plain value object: <see cref="SlotCalculator"/> turns it into UTC instants and
/// <see cref="AutopilotSettingsValidator"/> is the only place that decides whether a cadence is acceptable.
/// Nothing here re-checks the ranges, so a cadence built by hand in a test is taken at face value.
/// </para>
/// <para>
/// <see cref="DaysOfWeek"/> keeps the order the owner stored: the validator never reorders it, so
/// round-tripping <c>series.autopilot_settings</c> echoes the document. Nothing here relies on that order —
/// <see cref="SlotCalculator"/> tests membership per local date and
/// <see cref="AutopilotSettingsValidator.HorizonDays"/> sorts a copy for the wrap-gap interval — so a
/// hand-written cadence in any order behaves the same. <see cref="TimeZoneId"/> is an IANA id
/// (<c>Asia/Ho_Chi_Minh</c>), never a Windows id and never a fixed UTC offset: the offset is what DST changes
/// and the id is what survives it (plan §3.2).
/// </para>
/// </remarks>
/// <param name="DaysOfWeek">The weekdays the series publishes on; at least one, never a repeat.</param>
/// <param name="PublishTime">The local wall-clock publish time on each of those weekdays.</param>
/// <param name="TimeZoneId">The IANA time zone id the wall-clock time is read in.</param>
public sealed record AutopilotCadence(
    IReadOnlyList<DayOfWeek> DaysOfWeek,
    TimeOnly PublishTime,
    string TimeZoneId);
