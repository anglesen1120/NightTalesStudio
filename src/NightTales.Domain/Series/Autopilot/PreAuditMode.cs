using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// What an autopilot run does with its publication while the channel has not been audited yet (plan §3.1
/// <c>pre_audit_mode</c>, 02.8.3 plan §7.4). The wire spellings are <c>keep_cadence_private</c> and
/// <c>pause_after_n_private</c>.
/// </summary>
/// <remarks>
/// The mode is a planning decision, not a posting one: the run that creates the publication reads it, and the
/// upload keeps the publication <c>private</c> in both modes. The difference is what happens next — the first
/// mode leaves every publication locked private, the second lets the first
/// <see cref="AutopilotSettings.PreAuditPauseAfter"/> publications of the cadence go live and then pauses the
/// series for the owner's audit.
/// </remarks>
[JsonConverter(typeof(PreAuditModeJsonConverter))]
public enum PreAuditMode
{
    /// <summary>Every publication stays <c>private</c> (wire <c>keep_cadence_private</c>).</summary>
    KeepCadencePrivate = 0,

    /// <summary>The first N publications go live, then the series pauses for the audit (wire <c>pause_after_n_private</c>).</summary>
    PauseAfterNPrivate = 1,
}

/// <summary>The closed wire vocabulary of <see cref="PreAuditMode"/>, shared by the validator and the converter.</summary>
public static class PreAuditModeWire
{
    /// <summary>The wire name of <see cref="PreAuditMode.KeepCadencePrivate"/> (plan §3.1).</summary>
    public const string KeepCadencePrivate = "keep_cadence_private";

    /// <summary>The wire name of <see cref="PreAuditMode.PauseAfterNPrivate"/> (plan §3.1).</summary>
    public const string PauseAfterNPrivate = "pause_after_n_private";

    /// <summary>The wire name of one mode.</summary>
    /// <param name="mode">The mode to spell.</param>
    /// <returns>The snake_case wire name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a declared member.</exception>
    public static string ToWire(PreAuditMode mode) => mode switch
    {
        PreAuditMode.KeepCadencePrivate => KeepCadencePrivate,
        PreAuditMode.PauseAfterNPrivate => PauseAfterNPrivate,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown pre-audit mode."),
    };

    /// <summary>Reads one wire name.</summary>
    /// <param name="wire">The wire name.</param>
    /// <param name="mode">The mode, when <paramref name="wire"/> names one.</param>
    /// <returns>Whether <paramref name="wire"/> is one of the two names.</returns>
    public static bool TryParse(string? wire, out PreAuditMode mode)
    {
        switch (wire)
        {
            case KeepCadencePrivate:
                mode = PreAuditMode.KeepCadencePrivate;

                return true;
            case PauseAfterNPrivate:
                mode = PreAuditMode.PauseAfterNPrivate;

                return true;
            default:
                mode = default;

                return false;
        }
    }
}

/// <summary>
/// Reads and writes <see cref="PreAuditMode"/> as its snake_case wire name, so a settings document round-trips
/// through <see cref="AutopilotSettingsValidator.Serialize"/>. A value outside the closed list is a
/// <see cref="JsonException"/> rather than a silently wrong mode; the validator reports it as a field error.
/// </summary>
public sealed class PreAuditModeJsonConverter : JsonConverter<PreAuditMode>
{
    /// <inheritdoc />
    public override PreAuditMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A pre-audit mode must be a string.");
        }

        var wire = reader.GetString();

        return PreAuditModeWire.TryParse(wire, out var mode)
            ? mode
            : throw new JsonException($"Unknown pre-audit mode '{wire}'.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PreAuditMode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(PreAuditModeWire.ToWire(value));
    }
}
