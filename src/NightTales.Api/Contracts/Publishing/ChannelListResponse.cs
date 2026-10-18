using NightTales.Application.Features.Publishing.Channels;

namespace NightTales.Api.Contracts.Publishing;

/// <summary>
/// The body of <c>GET /api/v1/channels</c> (06.1.2-T2 task §Endpoint, 06 README D4/D6): every connection of the
/// calling account, newest first.
/// </summary>
/// <remarks>
/// <para>
/// <b>The envelope, not a bare array</b> (09 README D6): a list answers an object with an <c>items</c> member —
/// the same shape the catalog routes' <c>CatalogResponse&lt;T&gt;</c> and the paged routes' <c>Page&lt;T&gt;</c>
/// use — so a member added later (a count, a cursor) never breaks a client that already reads
/// <c>items</c>.
/// </para>
/// <para>
/// There is deliberately no paging and no cursor: one account has a handful of connections (06.1.2-T2 task
/// §Performance). The DTO carries no token and no <c>oauth_client_id</c>, so nothing here is a secret.
/// </para>
/// </remarks>
/// <param name="Items">The caller's connections, ordered by <c>connected_at</c> descending.</param>
public sealed record ChannelListResponse(IReadOnlyList<ChannelConnectionDto> Items);
