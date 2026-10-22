using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The cadence of a series' autopilot on the wire (02.8.3 plan §3.1 <c>autopilot_settings.cadence</c>):
/// the weekdays, the local publish time and the IANA time zone the two are read in.
/// </summary>
/// <remarks>
/// The spellings are the stored document's, never re-derived here: <see cref="AutopilotSettingsDto.From"/>
/// copies the members out of <c>AutopilotSettingsValidator.Serialize</c>'s output, so a weekday, a
/// <c>HH:mm</c> time and a zone id reach the client exactly as <c>series.autopilot_settings</c> holds them.
/// <para>
/// <b>The wire keys are the document's too.</b> Every member carries an explicit
/// <see cref="JsonPropertyNameAttribute"/>, because the write route hands the <c>settings</c> object it is given
/// straight to <see cref="NightTales.Domain.Series.Autopilot.AutopilotSettingsValidator"/>, which reads
/// <c>snake_case</c> and rejects a key it does not know (<c>RejectUnknownKeys</c>): without the attributes the read
/// answered <c>daysOfWeek</c> and the write demanded <c>days_of_week</c>, so a client could not put back what it just
/// read.
/// </para>
/// </remarks>
/// <param name="DaysOfWeek">The publishing weekdays, <c>mon</c> … <c>sun</c>, in the stored order.</param>
/// <param name="PublishTime">The local wall-clock publish time, <c>HH:mm</c>.</param>
/// <param name="TimeZone">The IANA zone id the wall-clock time is read in, e.g. <c>Asia/Ho_Chi_Minh</c>.</param>
public sealed record CadenceDto(
    [property: JsonPropertyName("days_of_week")] IReadOnlyList<string> DaysOfWeek,
    [property: JsonPropertyName("publish_time")] string PublishTime,
    [property: JsonPropertyName("time_zone")] string TimeZone)
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
