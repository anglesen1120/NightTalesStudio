using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.ReadModels;
using NightTales.Domain.Episodes;
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Stories.Queries.GetCurrentStory;

/// <summary>Reads the current story with the episode's lifecycle and approval pointers.</summary>
/// <param name="readDb">The tenant-filtered no-tracking read model.</param>
/// <param name="trueCrime">The validated <c>Story:TrueCrime</c> options: the attestation version a tick must name.</param>
/// <param name="attestation">The attestation clause text of the release.</param>
/// <param name="logger">Logs unreadable document ids only.</param>
public sealed class GetCurrentStoryHandler(
    IReadDb readDb,
    StoryTrueCrimeOptions trueCrime,
    IAttestationTextProvider attestation,
    ILogger<GetCurrentStoryHandler> logger)
    : IQueryHandler<GetCurrentStoryQuery, ErrorOr<CurrentStoryDto?>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<CurrentStoryDto?>> Handle(
        GetCurrentStoryQuery query, CancellationToken cancellationToken)
    {
        // The series' topic and language decide the attestation member, so they ride on this read (one
        // command, one round trip): the projection then costs one query for every episode, plus the
        // attestation rows of a true-crime one. `series` carries the same D10 tenant filter as the
        // episode, and `episodes.series_id` is a required FK, so the join never drops a live episode.
        var episode = await readDb.Query<EpisodeAggregate>().AsNoTracking()
            .Where(row => row.Id == query.EpisodeId && row.Status != EpisodeStatus.Cancelled)
            .Join(
                readDb.Query<SeriesAggregate>(),
                row => row.SeriesId,
                series => series.Id,
                (row, series) => new
                {
                    row.Status,
                    row.UserId,
                    series.Topic,
                    series.Language,
                    row.CurrentStoryVersionId,
                    row.ApprovedStoryVersionId,
                })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        var request = new StoryReadProjection.AttestationRequest(
            query.EpisodeId, episode.UserId, episode.Topic, episode.Language, trueCrime, attestation);
        var current = await StoryReadProjection.CurrentAsync(
            readDb, query.EpisodeId, episode.CurrentStoryVersionId, episode.Status,
            episode.ApprovedStoryVersionId, request, logger, cancellationToken).ConfigureAwait(false);
        return ErrorOrFactory.From(current);
    }
}
