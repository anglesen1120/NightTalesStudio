using NightTales.Application.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Dtos;

/// <summary>
/// Projects a <see cref="Publication"/> row onto the wire shape of the publication endpoints (06.2.1-T1 task
/// §Design): the create command answers the row it just wrote, <c>GET /episodes/{id}/publications</c> answers
/// every row of the episode, and both go through this one mapper, so the two answers cannot drift.
/// </summary>
/// <remarks>
/// The mapper is <c>static</c> because it holds no state and needs none of the scope's services: every member
/// comes from the row and the connection's display name, the <c>ETag</c> is derived from the row's <c>xmin</c>
/// instead of being stored, and the two pre-audit members come out of <c>platform_payload</c> — the raw column
/// never leaves this file (06 README D17).
/// </remarks>
internal static class PublicationDtoMapper
{
    /// <summary>Projects one row.</summary>
    /// <param name="publication">The tracked row; its <c>Version</c> carries the <c>xmin</c> EF read back.</param>
    /// <param name="channelDisplayName">The connection's display name, or <see langword="null"/> for a purged row.</param>
    /// <returns>The wire shape of the row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is <see langword="null"/>.</exception>
    internal static PublicationDto ToDto(Publication publication, string? channelDisplayName)
    {
        ArgumentNullException.ThrowIfNull(publication);

        var preAudit = PublicationPlatformPayload.ReadPreAudit(publication.PlatformPayloadJson);

        return new PublicationDto(
            publication.Id,
            publication.EpisodeId,
            new PublicationChannelDto(publication.ChannelConnectionId, channelDisplayName),
            PublicationWire.Status(publication.Status),
            publication.Title,
            publication.Description,
            publication.Tags,
            publication.Privacy,
            preAudit is not null,
            preAudit?.RequestedPrivacy,
            publication.PublishAt,
            publication.VetoEndsAt,
            publication.ConsentAt,
            PublicationWire.ConsentSource(publication.ConsentSource),
            publication.ExternalPostId,
            publication.ExternalUrl,
            publication.ErrorCode,
            publication.NextAttemptAt,
            ETags.Publication(publication.Version));
    }
}
