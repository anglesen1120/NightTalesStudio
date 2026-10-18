using NightTales.Application.Features.Publishing.Dtos;

namespace NightTales.Api.Contracts.Publishing;

/// <summary>
/// The body of <c>GET /api/v1/episodes/{id}/publications</c> (06.2.1-T1 task §API, 06 README D6): every
/// publication of the episode, newest first.
/// </summary>
/// <remarks>
/// <para>
/// The envelope, not a bare array (09 README D6): a list answers an object with an <c>items</c> member — the same
/// shape <c>ChannelListResponse</c> and the paged routes' <c>Page&lt;T&gt;</c> use — so a member added later (a
/// count, a cursor) never breaks a client that already reads <c>items</c>.
/// </para>
/// <para>
/// There is deliberately no paging and no cursor: an episode has a handful of publications, one per platform post
/// (06.2.1 task §API). No DTO member carries a token, a protected payload or an authorization id.
/// </para>
/// </remarks>
/// <param name="Items">The episode's publications, ordered by <c>created_at</c> descending and then by id.</param>
public sealed record PublicationListResponse(IReadOnlyList<PublicationDto> Items);
