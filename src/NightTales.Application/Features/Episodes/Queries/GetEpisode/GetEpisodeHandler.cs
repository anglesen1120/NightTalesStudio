using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

// The feature namespace `…Features.Episodes` contains the aggregate's namespace segment, so the entity
// is aliased the way the series handlers alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// <c>GET /api/v1/episodes/{id}</c> (09.5.1 plan §4 UC3): one projection through the tenant-filtered read
/// model, so another user's episode is a 404 and never a 403 (09 README D10), and a cancelled episode is
/// invisible to reads (F14).
/// </summary>
/// <remarks>
/// The ETag is built from the row's <c>xmin</c>, which the D8 convention maps to
/// <see cref="AggregateRoot.Version"/>: a read returns the value that is current <em>now</em>, which is
/// exactly what <c>If-Match</c> on the next write must compare against.
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
public sealed class GetEpisodeHandler(IReadDb readDb) : IQueryHandler<GetEpisodeQuery, ErrorOr<EpisodeDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<EpisodeDto>> Handle(
        GetEpisodeQuery query,
        CancellationToken cancellationToken)
    {
        var row = await readDb.Query<EpisodeAggregate>()
            .Where(episode => episode.Id == query.Id && episode.Status != EpisodeStatus.Cancelled)
            .Select(episode => new Row(
                episode.Id,
                episode.SeriesId,
                episode.Title,
                episode.Brief,
                episode.Status,
                episode.LengthTier,
                episode.CreatedAt,
                episode.UpdatedAt,
                episode.Version))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? Errors.NotFound()
            : new EpisodeDto(
                row.Id,
                row.SeriesId,
                row.Title,
                row.Brief,
                row.Status,
                row.LengthTier,
                row.CreatedAt,
                row.UpdatedAt,
                ETags.Episode(row.Version));
    }

    /// <summary>The projected row, including the concurrency token the ETag is built from.</summary>
    private sealed record Row(
        Guid Id,
        Guid SeriesId,
        string? Title,
        string Brief,
        EpisodeStatus Status,
        LengthTier LengthTier,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        uint Version);
}
