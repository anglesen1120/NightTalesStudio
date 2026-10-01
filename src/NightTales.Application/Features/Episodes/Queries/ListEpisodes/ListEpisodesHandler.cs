using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Common.Paging;
using NightTales.Domain.Episodes;

// The feature namespace `…Features.Episodes` contains the aggregate's namespace segment, so the entity
// is aliased the way the series handlers alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Episodes.Queries.ListEpisodes;

/// <summary>
/// <c>GET /api/v1/episodes</c> (09.5.1 plan §4 UC2, Design): the keyset page
/// <c>WHERE (created_at, id) &lt; (@c, @i) ORDER BY created_at DESC, id DESC LIMIT @limit + 1</c>, whose
/// extra row is what tells the handler whether a next page exists.
/// </summary>
/// <remarks>
/// <para>
/// Cancelled episodes are excluded unconditionally (F14): they are invisible to reads, on this route and
/// on <c>GET /episodes/{id}</c> alike. The optional <c>seriesId</c> and <c>status</c> filters narrow what
/// is left, and they compose with the exclusion — asking for <c>status=cancelled</c> therefore returns an
/// empty page rather than the cancelled rows.
/// </para>
/// <para>
/// The comparison is the row-value comparison PostgreSQL optimizes against the composite index
/// <c>(user_id, created_at DESC, id DESC)</c> (or <c>(user_id, status, created_at DESC, id DESC)</c> /
/// <c>(series_id, created_at DESC, id DESC)</c>, 09.2.2-T1 §9): Npgsql renders the tuple comparison as
/// <c>(created_at, id) &lt; (@c, @i)</c>, one range the index can seek, and the pair is the total order,
/// so a row inserted between two requests can neither duplicate nor skip one (09 README D14).
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
public sealed class ListEpisodesHandler(IReadDb readDb)
    : IQueryHandler<ListEpisodesQuery, ErrorOr<Page<EpisodeListItemDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Page<EpisodeListItemDto>>> Handle(
        ListEpisodesQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > ListEpisodesQuery.MaxLimit)
        {
            // The validator already answered this for an HTTP caller; a direct handler call reaches the
            // same 400 instead of paging with a negative Take.
            return Errors.ValidationFailed(
                "limit",
                $"The page size must be between 1 and {ListEpisodesQuery.MaxLimit}.");
        }

        var rows = readDb.Query<EpisodeAggregate>()
            .Where(episode => episode.Status != EpisodeStatus.Cancelled);

        if (query.SeriesId is { } seriesId)
        {
            rows = rows.Where(episode => episode.SeriesId == seriesId);
        }

        if (query.Status is { } status)
        {
            rows = rows.Where(episode => episode.Status == status);
        }

        if (!string.IsNullOrEmpty(query.Cursor))
        {
            if (!Cursor.TryDecode(query.Cursor, out var cursor))
            {
                return Errors.ValidationFailed("cursor", "The cursor is not a valid page cursor.");
            }

            var beforeCreatedAt = cursor.CreatedAt;
            var beforeId = cursor.Id;

            rows = rows.Where(episode => ValueTuple.Create(episode.CreatedAt, episode.Id)
                .CompareTo(ValueTuple.Create(beforeCreatedAt, beforeId)) < 0);
        }

        var page = await rows
            .OrderByDescending(episode => episode.CreatedAt)
            .ThenByDescending(episode => episode.Id)
            .Take(query.Limit + 1)
            .Select(episode => new Row(
                episode.Id,
                episode.SeriesId,
                episode.Title,
                episode.Status,
                episode.LengthTier,
                episode.CreatedAt,
                episode.UpdatedAt,
                episode.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = page
            .Take(query.Limit)
            .Select(row => new EpisodeListItemDto(
                row.Id,
                row.SeriesId,
                row.Title,
                row.Status,
                row.LengthTier,
                row.CreatedAt,
                row.UpdatedAt,
                ETags.Episode(row.Version)))
            .ToList();

        var nextCursor = page.Count > query.Limit && items.Count > 0
            ? new Cursor(items[^1].CreatedAt, items[^1].Id).Encode()
            : null;

        return new Page<EpisodeListItemDto>(items, nextCursor);
    }

    /// <summary>The projected row, including the concurrency token each item's ETag is built from.</summary>
    private sealed record Row(
        Guid Id,
        Guid SeriesId,
        string? Title,
        EpisodeStatus Status,
        LengthTier LengthTier,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        uint Version);
}
