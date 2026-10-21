using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels;

/// <summary>
/// One connection of the caller, as <c>GET /channels</c> answers it (06.1.2-T2 task §Design, 06 README D4/D6):
/// the channel identity, the connection's state and the two instants the page shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>No secret and no pinning detail</b> (06 README D17): no token column, no <c>oauth_client_id</c>, no
/// <c>scope</c> — the client the connection is pinned to is internal, and the S-phase admin view is
/// 06.1.2-T3's. The row of a revoked connection still reads back (the list shows the history), which is why
/// <see cref="ExternalAccountId"/>, <see cref="DisplayName"/> and <see cref="LastVerifiedAt"/> are nullable:
/// a purge (06.4.3-T1) nulls the identity of an already-revoked row.
/// </para>
/// <para>
/// The two strings are <b>wire values, not enum members</b>: <see cref="Platform"/> is <c>youtube</c>/<c>tiktok</c>
/// (<c>PlatformExtensions.ToWire</c>) and <see cref="Status"/> is
/// <c>active</c>/<c>revoked</c>/<c>expired</c>/<c>error</c> — the same lower-case spelling the column, the CHECK
/// constraint and the JSON of every other enum in the API use, so the web page compares against one form.
/// </para>
/// </remarks>
/// <param name="Id">The <c>channel_connections.id</c>: what a disconnect route and the upload path address.</param>
/// <param name="Platform">The platform wire value (<c>youtube</c>).</param>
/// <param name="ExternalAccountId">Google's channel id, or <see langword="null"/> for a purged row.</param>
/// <param name="DisplayName">The channel title, or <see langword="null"/> for a purged row.</param>
/// <param name="Status">The status wire value (<c>active</c>, <c>revoked</c>, <c>expired</c>, <c>error</c>).</param>
/// <param name="LastVerifiedAt">When the connection last proved its tokens, or <see langword="null"/> if never.</param>
/// <param name="ConnectedAt">When the connection was first made; unchanged by a reconnect.</param>
public sealed record ChannelConnectionDto(
    Guid Id,
    string Platform,
    string? ExternalAccountId,
    string? DisplayName,
    string Status,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset ConnectedAt);

/// <summary>
/// The wire spelling of <see cref="ChannelConnectionStatus"/> as the API contract states it (06 README D4, 06.1.2-T2
/// task §Design): lower case, one word, exactly the text the column stores and
/// <c>ck_channel_connections_status</c> admits.
/// </summary>
/// <remarks>
/// The mapping is hand-written and exhaustive on purpose — the reason <c>PlatformExtensions.ToWire</c> is too:
/// the JSON members are strings, so the serializer's own enum converter (which the API does configure for the
/// enums it serializes directly) never sees this value, and a mapping that silently fell back to
/// <c>ToString()</c> would drift from the stored text the web page compares against. A member added to the enum
/// without a wire value throws here instead of shipping an unmapped string.
/// </remarks>
internal static class ChannelConnectionWire
{
    /// <summary>The wire value of <paramref name="status"/>.</summary>
    /// <param name="status">The stored status.</param>
    /// <returns><c>active</c>, <c>revoked</c>, <c>expired</c> or <c>error</c>.</returns>
    internal static string Status(ChannelConnectionStatus status) => status switch
    {
        ChannelConnectionStatus.Active => "active",
        ChannelConnectionStatus.Revoked => "revoked",
        ChannelConnectionStatus.Expired => "expired",
        ChannelConnectionStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "No wire value is declared for this status."),
    };
}
