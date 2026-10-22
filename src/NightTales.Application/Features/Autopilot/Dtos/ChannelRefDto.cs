namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The channel a series' autopilot publishes to (02.8.3 plan §3.7, <c>channel_connection_id</c>): the row the
/// authorization is bound to, its display title and its connection status.
/// </summary>
/// <remarks>
/// <para>
/// <b>The display title is the one member the text carries that the settings do not.</b> The consent sentence
/// names the channel by title, so a retitled channel changes the rendered text; it does not revoke the
/// authorization, because renames are not a covered field (plan §3.7 names
/// <c>series.default_channel_id</c> only, not the title).
/// </para>
/// <para>
/// <b>The status is the slug</b> the AGENTS.md floor requires on the wire (<c>active</c>, <c>needs_reauth</c>,
/// …), the same value <c>channel_connections.status</c> stores.
/// </para>
/// <para>
/// A series with no <c>default_channel_id</c> answers <see langword="null"/> from
/// <see cref="AutopilotDto.Channel"/> instead of this record: there is no channel to name, and inventing a
/// placeholder title would put words in a consent text.
/// </para>
/// </remarks>
/// <param name="Id">The <c>channel_connections</c> id, which is also the id the covered comparison binds to.</param>
/// <param name="Title">The channel's display title, <see langword="null"/> before the first metadata sync.</param>
/// <param name="Status">The connection status slug, e.g. <c>active</c>.</param>
public sealed record ChannelRefDto(Guid Id, string? Title, string Status);
