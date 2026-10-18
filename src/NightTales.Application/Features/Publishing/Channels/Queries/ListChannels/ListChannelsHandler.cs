using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Channels.Queries.ListChannels;

/// <summary>
/// The caller's channel connections, newest first (06.1.2-T2 task §Design and §Performance, 06 README D4/D6).
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement.</b> A single <c>SELECT</c> that projects the DTO's columns, ordered by
/// <c>connected_at DESC</c> and served by <c>ix_channel_connections_user_id</c> combined with the 09 README D10
/// tenant filter: no <c>Include</c>, no navigation, no count, no paging and no per-row follow-up — a user has a
/// handful of connections. A test asserts the one-command shape.
/// </para>
/// <para>
/// <b>No secret is read.</b> The projection touches neither token column nor <c>oauth_client_id</c>, so the
/// protector is off this path entirely: decryption happens only where a Google call needs a live token
/// (<see cref="NightTales.Application.Abstractions.Publishing.ITokenService"/>).
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model of 09 README D8; its tenant filter is what scopes the list.</param>
public sealed class ListChannelsHandler(IReadDb readDb)
    : IQueryHandler<ListChannelsQuery, ErrorOr<IReadOnlyList<ChannelConnectionDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<ChannelConnectionDto>>> Handle(
        ListChannelsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The two wire values are mapped inside the projection: EF evaluates them client-side once the row's
        // columns are read (client evaluation in the top-level projection), so the statement stays the one indexed
        // SELECT and no second row type is needed beside the DTO.
        var channels = await readDb
            .Query<ChannelConnection>()
            .OrderByDescending(connection => connection.ConnectedAt)
            .Select(connection => new ChannelConnectionDto(
                connection.Id,
                connection.Platform.ToWire(),
                connection.ExternalAccountId,
                connection.DisplayName,
                ChannelConnectionWire.Status(connection.Status),
                connection.LastVerifiedAt,
                connection.ConnectedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return channels;
    }
}
