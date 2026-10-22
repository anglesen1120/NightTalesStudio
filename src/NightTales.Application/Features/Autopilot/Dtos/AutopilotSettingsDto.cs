using System.Text.Json;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The settings document of <c>GET /series/{id}/autopilot</c> (02.8.3 plan §3.1), typed for the client.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source of spellings.</b> <see cref="From"/> reads the members out of
/// <see cref="AutopilotSettingsValidator.Serialize"/>, the same document the row stores — the trick
/// <see cref="NightTales.Domain.Series.Autopilot.AutopilotAuthorizationCoverage.SnapshotJson"/> uses. A
/// weekday, a privacy and a pre-audit mode therefore cannot drift between what the client shows, what the
/// owner writes back and what the covered-field snapshot compares (plan §3.7).
/// </para>
/// <para>
/// The members are the full §3.1 schema, not only the ones the authorization text covers: the settings form
/// edits all of them, and the covered-field rule belongs to the revoke comparison, not to this projection.
/// </para>
/// </remarks>
/// <param name="Cadence">The weekdays, local publish time and time zone the slots are computed from.</param>
/// <param name="DailyCap">Publications per channel per local day, 1..5.</param>
/// <param name="BufferDays">Ready-buffer depth and planning horizon, 1..7.</param>
/// <param name="Privacy">The publication privacy, <c>public</c>|<c>unlisted</c>|<c>private</c>.</param>
/// <param name="VetoWindowHours">How long the owner has to veto before the upload, 2..48.</param>
/// <param name="PreAuditMode">What happens to a publication while the channel is unaudited.</param>
/// <param name="PreAuditPauseAfter">The N of <c>pause_after_n_private</c>, 1..20.</param>
/// <param name="GateThresholdsRef">The gate-profile name 02.8.2 reads; <c>default</c> is the shipped profile.</param>
/// <param name="Tone">The tone brief 02.8.1 feeds the story prompt with.</param>
/// <param name="BannedThemes">The themes the story prompt must avoid.</param>
/// <param name="RenderLeadMinutes">The render lead override, or <see langword="null"/> for the configured estimate.</param>
/// <param name="StoryLeadMinutes">The story lead override, or <see langword="null"/> for the configured estimate.</param>
public sealed record AutopilotSettingsDto(
    CadenceDto Cadence,
    int DailyCap,
    int BufferDays,
    string Privacy,
    int VetoWindowHours,
    string PreAuditMode,
    int PreAuditPauseAfter,
    string GateThresholdsRef,
    string Tone,
    IReadOnlyList<string> BannedThemes,
    int? RenderLeadMinutes,
    int? StoryLeadMinutes)
{
    /// <summary>Projects a validated settings record onto the wire shape.</summary>
    /// <param name="settings">The settings a validated document produced.</param>
    /// <returns>The settings as the wire carries them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static AutopilotSettingsDto From(AutopilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var document = JsonDocument.Parse(AutopilotSettingsValidator.Serialize(settings));
        var root = document.RootElement;

        var themes = new List<string>();
        foreach (var theme in root.GetProperty("banned_themes").EnumerateArray())
        {
            themes.Add(theme.GetString() ?? string.Empty);
        }

        return new AutopilotSettingsDto(
            CadenceDto.From(root.GetProperty("cadence")),
            root.GetProperty("daily_cap").GetInt32(),
            root.GetProperty("buffer_days").GetInt32(),
            root.GetProperty("privacy").GetString() ?? string.Empty,
            root.GetProperty("veto_window_hours").GetInt32(),
            root.GetProperty("pre_audit_mode").GetString() ?? string.Empty,
            root.GetProperty("pre_audit_pause_after").GetInt32(),
            root.GetProperty("gate_thresholds_ref").GetString() ?? string.Empty,
            root.GetProperty("tone").GetString() ?? string.Empty,
            themes,
            ReadOptionalInt(root, "render_lead_minutes"),
            ReadOptionalInt(root, "story_lead_minutes"));
    }

    /// <summary>Reads an optional integer member; <see cref="JsonValueKind.Null"/> is <see langword="null"/>.</summary>
    /// <param name="root">The settings document's root.</param>
    /// <param name="member">The member name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static int? ReadOptionalInt(JsonElement root, string member) =>
        root.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
