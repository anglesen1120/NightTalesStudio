using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The whole autopilot state of one series as <c>GET /series/{id}/autopilot</c> answers it (02.8.3 plan §4 UC1,
/// task file DTOs): what is configured, what is authorized, whether the owner must accept again, and what the
/// next slots are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three members are nullable because three states have no value.</b> A series whose
/// <c>autopilot_settings</c> is absent has no <see cref="Settings"/>; a series with no
/// <c>default_channel_id</c> has no <see cref="Channel"/>; and with either of those absent there is nothing to
/// render, so <see cref="AuthorizationText"/> is <see langword="null"/> too. The task file declares the three
/// non-nullable; answering fabricated members instead (an empty settings document, a placeholder channel title,
/// an empty consent sentence) would put words into a legal text that nobody accepted, which is the one thing
/// plan §3.7 forbids. <see cref="RequiredAuthorization"/> is <see langword="true"/> in every such state, so a
/// client that only reads that flag is correct.
/// </para>
/// <para>
/// <b><see cref="RequiredAuthorization"/> is computed, never stored.</b> It is the covered-field comparison the
/// write route performs, run without side effects: no authorization, or a live one whose snapshot differs from
/// the current settings, topic and channel, means the owner must accept again (plan §3.7). A client may call
/// this route as often as it likes; nothing is revoked by a read.
/// </para>
/// </remarks>
/// <param name="Enabled">Whether autopilot is on for this series.</param>
/// <param name="Settings">The stored settings document, or <see langword="null"/> when none was saved yet.</param>
/// <param name="PausedAt">When the autopilot last paused, or <see langword="null"/> when it is running (plan §3.6).</param>
/// <param name="PauseReason">The closed pause reason slug, or <see langword="null"/> when there is none.</param>
/// <param name="Authorization">The live authorization, or <see langword="null"/> when none is current.</param>
/// <param name="AuthorizationText">The text the owner may accept now, or <see langword="null"/> when it cannot be rendered.</param>
/// <param name="RequiredAuthorization">Whether the owner must accept the current text before autopilot can run.</param>
/// <param name="Channel">The default channel, or <see langword="null"/> when the series has none.</param>
/// <param name="GatesDryRun">Whether the 02.8.2 gate profile runs in dry-run mode (read-only here, plan §3.8).</param>
/// <param name="GatesDryRunRemaining">The remaining dry-run decisions of the current window.</param>
/// <param name="NextSlots">The next three planned slots, soonest first.</param>
public sealed record AutopilotDto(
    bool Enabled,
    AutopilotSettingsDto? Settings,
    DateTimeOffset? PausedAt,
    string? PauseReason,
    AuthorizationDto? Authorization,
    AuthorizationTextDto? AuthorizationText,
    bool RequiredAuthorization,
    ChannelRefDto? Channel,
    bool GatesDryRun,
    int GatesDryRunRemaining,
    IReadOnlyList<NextSlotDto> NextSlots);
