using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Queries.ListSourceCandidates;

/// <summary>
/// <c>GET /api/v1/episodes/{episodeId}/sources/candidates</c>: a no-tracking projection of the episode's candidates
/// through the tenant-filtered read model (02.7.2-T2 R9/R14).
/// </summary>
/// <remarks>
/// Another user's episode, an unknown id and a cancelled episode all answer 404 — the tenant filter answers the first
/// two before the explicit check, and an anonymous scope matches no row at all. The order is the plan's: the latest
/// find run first (<c>created_at</c> desc) and each run's own ranking within it (<c>rank</c> asc), with the id only as
/// a deterministic tie-break.
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies.</param>
public sealed class ListSourceCandidatesHandler(IReadDb readDb)
    : IQueryHandler<ListSourceCandidatesQuery, ErrorOr<SourceCandidateListDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceCandidateListDto>> Handle(
        ListSourceCandidatesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var exists = await readDb.Query<Episode>()
            .AnyAsync(
                episode => episode.Id == query.EpisodeId && episode.Status != EpisodeStatus.Cancelled,
                cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return Errors.NotFound();
        }

        var rows = await readDb.Query<SourceCandidate>()
            .Where(candidate => candidate.EpisodeId == query.EpisodeId)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Url,
                candidate.Title,
                candidate.SiteName,
                candidate.Rank,
                candidate.Status,
                candidate.SourceId,
                candidate.CreatedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => new SourceCandidateDto(
                row.Id,
                row.Url,
                row.Title,
                row.SiteName,
                row.Rank,
                WireStatus(row.Status),
                row.SourceId,
                row.CreatedAt))
            .ToList();

        return new SourceCandidateListDto(items);
    }

    /// <summary>The snake_case wire value of a status (R9): <c>proposed</c>, <c>added</c> or <c>dismissed</c>.</summary>
    private static string WireStatus(SourceCandidateStatus status) => status switch
    {
        SourceCandidateStatus.Proposed => "proposed",
        SourceCandidateStatus.Added => "added",
        SourceCandidateStatus.Dismissed => "dismissed",
        _ => status.ToString().ToLowerInvariant(),
    };
}
