using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.ReadModels;
using NightTales.Domain.Episodes;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Stories.Queries.GetCurrentStory;

/// <summary>Reads the current story with the episode's lifecycle and approval pointers.</summary>
/// <param name="readDb">The tenant-filtered no-tracking read model.</param>
/// <param name="logger">Logs unreadable document ids only.</param>
public sealed class GetCurrentStoryHandler(IReadDb readDb, ILogger<GetCurrentStoryHandler> logger)
    : IQueryHandler<GetCurrentStoryQuery, ErrorOr<CurrentStoryDto?>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<CurrentStoryDto?>> Handle(
        GetCurrentStoryQuery query, CancellationToken cancellationToken)
    {
        var episode = await readDb.Query<EpisodeAggregate>().AsNoTracking()
            .Where(row => row.Id == query.EpisodeId && row.Status != EpisodeStatus.Cancelled)
            .Select(row => new { row.Status, row.CurrentStoryVersionId, row.ApprovedStoryVersionId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        var current = await StoryReadProjection.CurrentAsync(
            readDb, query.EpisodeId, episode.CurrentStoryVersionId, episode.Status,
            episode.ApprovedStoryVersionId, logger, cancellationToken).ConfigureAwait(false);
        return ErrorOrFactory.From(current);
    }
}
