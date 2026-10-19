using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Queries.GetEpisodePublications;

/// <summary>
/// Reads one episode's publications (06.2.1-T1 task §Application Queries, 06 README D6): the caller's own rows,
/// newest first, each with the connection it uploads through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner-scoped and absent-safe</b> (09 README D10): the existence check runs through the tenant-filtered read
/// model, so an unknown id and another account's id both answer 404 <c>not_found</c> — never 403, and never an
/// empty list that would confirm the episode exists.
/// </para>
/// <para>
/// <b>One round trip for the list.</b> The channel display name is joined in the same <c>SELECT</c> as the
/// publications, so a page of rows costs one statement rather than one per row — the DTO carries no navigation.
/// The order is <c>created_at</c> descending and then id, so two rows created in the same instant still read back
/// in a stable order. No index carries that ordering: the planner serves the episode and tenant filter from
/// <c>ix_publications_episode_id_user_id</c> and sorts one episode's rows in memory.
/// </para>
/// </remarks>
/// <param name="readDb">The tenant-filtered read model.</param>
public sealed class GetEpisodePublicationsHandler(IReadDb readDb)
    : IQueryHandler<GetEpisodePublicationsQuery, ErrorOr<IReadOnlyList<PublicationDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<PublicationDto>>> Handle(
        GetEpisodePublicationsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var episodeExists = await readDb.Query<Episode>()
            .AnyAsync(row => row.Id == query.EpisodeId, cancellationToken)
            .ConfigureAwait(false);

        if (!episodeExists)
        {
            return Errors.NotFound();
        }

        var rows = await readDb.Query<Publication>()
            .Where(publication => publication.EpisodeId == query.EpisodeId)
            .OrderByDescending(publication => publication.CreatedAt)
            .ThenByDescending(publication => publication.Id)
            .Join(
                readDb.Query<ChannelConnection>(),
                publication => publication.ChannelConnectionId,
                connection => connection.Id,
                (publication, connection) => new PublicationRow(publication, connection.DisplayName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => PublicationDtoMapper.ToDto(row.Publication, row.ChannelDisplayName))
            .ToList();
    }

    /// <summary>One publication with the display name of the connection it uploads through.</summary>
    /// <param name="Publication">The row itself.</param>
    /// <param name="ChannelDisplayName">The channel title, or <see langword="null"/> for a purged connection.</param>
    private sealed record PublicationRow(Publication Publication, string? ChannelDisplayName);
}
