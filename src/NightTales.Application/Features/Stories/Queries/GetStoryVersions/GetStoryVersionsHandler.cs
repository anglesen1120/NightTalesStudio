using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.ReadModels;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Stories.Queries.GetStoryVersions;

/// <summary>Reads history metadata only; tenant-visible episode existence is checked first.</summary>
/// <param name="readDb">The tenant-filtered no-tracking read model.</param>
public sealed class GetStoryVersionsHandler(IReadDb readDb)
    : IQueryHandler<GetStoryVersionsQuery, ErrorOr<IReadOnlyList<StoryVersionSummaryDto>>>
{
    /// <summary>
    /// At most 200 newest versions are returned. There is no storage version cap; this bounds this
    /// non-paginated editor history response without loading any content or audit documents.
    /// </summary>
    public const int MaximumHistoryVersions = 200;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<StoryVersionSummaryDto>>> Handle(
        GetStoryVersionsQuery query, CancellationToken cancellationToken)
    {
        var episode = await readDb.Query<EpisodeAggregate>().AsNoTracking()
            .Where(row => row.Id == query.EpisodeId && row.Status != EpisodeStatus.Cancelled)
            .Select(row => new { row.CurrentStoryVersionId, row.ApprovedStoryVersionId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        var rows = await readDb.Query<StoryVersion>().AsNoTracking()
            .Where(row => row.EpisodeId == query.EpisodeId)
            .OrderByDescending(row => row.Version)
            .Take(MaximumHistoryVersions)
            .Select(row => new SummaryRow(
                row.Id, row.Version, row.Source, row.CreatedAt, row.WordCount, row.SimilarityScore))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoryVersionSummaryDto> result = rows.Select(row => new StoryVersionSummaryDto(
            row.VersionId, row.Version, StoryReadProjection.Wire(row.Source), row.CreatedAt,
            row.WordCount, StoryEtag.For(row.VersionId), row.SimilarityScore,
            episode.ApprovedStoryVersionId == row.VersionId, episode.CurrentStoryVersionId == row.VersionId))
            .ToList();
        return ErrorOrFactory.From(result);
    }

    private sealed record SummaryRow(
        Guid VersionId, int Version, StoryVersionSource Source,
        DateTimeOffset CreatedAt, int WordCount, decimal? SimilarityScore);
}
