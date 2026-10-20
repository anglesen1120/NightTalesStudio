using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// The stored form of <see cref="AutopilotPauseReason"/> (02.8.3 plan §3.6): the snake_case wire value of every
/// member, with the one exception the plan names — <see cref="AutopilotPauseReason.ChannelUnsupported"/> is stored as
/// <c>autopilot_channel_unsupported</c> (row 6b), where the generic snake_case rule would spell
/// <c>channel_unsupported</c>. <c>autopilot_runs.pause_reason</c> is the one column that carries the enum itself, so
/// both its value conversion and its <c>CHECK</c> read this single table: the stored values and the allowed values
/// cannot drift.
/// </summary>
internal static class AutopilotPauseReasonWire
{
    /// <summary>The plan §3.6 row 6b wire value of <see cref="AutopilotPauseReason.ChannelUnsupported"/>.</summary>
    internal const string ChannelUnsupported = "autopilot_channel_unsupported";

    private static readonly Dictionary<AutopilotPauseReason, string> ToWireValues = Enum
        .GetValues<AutopilotPauseReason>()
        .ToDictionary(reason => reason, Wire);

    private static readonly Dictionary<string, AutopilotPauseReason> FromWireValues = ToWireValues
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    /// <summary>Every accepted wire value, ordinal order: the <c>ck_autopilot_runs_pause_reason</c> list.</summary>
    internal static IReadOnlyList<string> All { get; } =
        [.. ToWireValues.Values.Order(StringComparer.Ordinal)];

    /// <summary>The stored text of <paramref name="reason"/>.</summary>
    internal static string ToWire(AutopilotPauseReason reason) => ToWireValues[reason];

    /// <summary>The member of the stored text <paramref name="value"/>.</summary>
    /// <exception cref="KeyNotFoundException">The text is not one of <see cref="All"/>.</exception>
    internal static AutopilotPauseReason FromWire(string value) => FromWireValues[value];

    private static string Wire(AutopilotPauseReason reason) =>
        reason == AutopilotPauseReason.ChannelUnsupported
            ? ChannelUnsupported
            : EnumSnakeCase.Convert(reason.ToString());
}

/// <summary>
/// The <c>autopilot_runs.pause_reason</c> conversion (02.8.3 plan §3.3): the enum to
/// <see cref="AutopilotPauseReasonWire"/>'s stored text and back, so the aggregate keeps the member and the column
/// keeps the wire value the alert copy key and the <c>CHECK</c> use.
/// </summary>
internal sealed class AutopilotPauseReasonConverter : ValueConverter<AutopilotPauseReason, string>
{
    public AutopilotPauseReasonConverter()
        : base(reason => AutopilotPauseReasonWire.ToWire(reason), value => AutopilotPauseReasonWire.FromWire(value))
    {
    }
}
