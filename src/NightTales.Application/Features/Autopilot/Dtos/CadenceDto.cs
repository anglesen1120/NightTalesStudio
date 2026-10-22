using System.Text.Json;

namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The cadence of a series' autopilot on the wire (02.8.3 plan §3.1 <c>autopilot_settings.cadence</c>):
/// the weekdays, the local publish time and the IANA time zone the two are read in.
/// </summary>
/// <remarks>
/// The spellings are the stored document's, never re-derived here: <see cref="AutopilotSettingsDto.From"/>
/// copies the members out of <c>AutopilotSettingsValidator.Serialize</c>'s output, so a weekday, a
/// <c>HH:mm</c> time and a zone id reach the client exactly as <c>series.autopilot_settings</c> holds them.
/// </remarks>
/// <param name="DaysOfWeek">The publishing weekdays, <c>mon</c> … <c>sun</c>, in the stored order.</param>
/// <param name="PublishTime">The local wall-clock publish time, <c>HH:mm</c>.</param>
/// <param name="TimeZone">The IANA zone id the wall-clock time is read in, e.g. <c>Asia/Ho_Chi_Minh</c>.</param>
public sealed record CadenceDto(IReadOnlyList<string> DaysOfWeek, string PublishTime, string TimeZone)
{
    /// <summary>Projects the <c>cadence</c> member of a serialized settings document.</summary>
    /// <param name="cadence">The document's <c>cadence</c> object.</param>
    /// <returns>The cadence as the wire carries it.</returns>
    internal static CadenceDto From(JsonElement cadence)
    {
        var days = new List<string>();
        foreach (var day in cadence.GetProperty("days_of_week").EnumerateArray())
        {
            days.Add(day.GetString() ?? string.Empty);
        }

        return new CadenceDto(
            days,
            cadence.GetProperty("publish_time").GetString() ?? string.Empty,
            cadence.GetProperty("time_zone").GetString() ?? string.Empty);
    }
}
