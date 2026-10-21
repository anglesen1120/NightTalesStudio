using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.ReadModels;
using NightTales.Domain.Assets;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Stories.Generation;

// The feature namespace `…Features.Episodes` contains the aggregate's namespace segment, so the entity
// is aliased the way the series handlers alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

// 02.3.3-T1: the series' topic and language ride on the episode read (the attestation member).
using SeriesAggregate = NightTales.Domain.Series.Series;
using Topic = NightTales.Domain.Series.Topic;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// <c>GET /api/v1/episodes/{id}</c> (09.5.3 plan §4 UC3, §5): the episode page in one round trip — the
/// episode's own columns with its <c>xmin</c>, the actions the domain allows next, the final video
/// metadata the episode points at — or a 404.
/// </summary>
/// <remarks>
/// <para>
/// One projection through the tenant-filtered read model, followed by bounded single-row reads for
/// the final video, current story and latest generation run, sequentially on the same
/// <see cref="IReadDb"/> (BD16: EF forbids concurrent operations on one context).
/// 02.3.4-T2 extends BD16 with one read each for the latest generation run and the current version;
/// the current-version read is skipped when its pointer is null. No <c>Include</c>, no N+1, and no
/// nested mediator pipeline. Another user's episode, an unknown id and a cancelled row all answer
/// the same 404 (09 README D10, F14).
/// </para>
/// <para>
/// The ETag is built from the row's <c>xmin</c>, which the D8 convention maps to
/// <c>AggregateRoot.Version</c>: a read returns the value that is current <em>now</em>, which is
/// exactly what <c>If-Match</c> on the next write must compare against (09 README D15).
/// </para>
/// <para>
/// Extension points, one route and one projection each — never a second handler for this resource
/// (09.5.3 plan §Steps 6): 09.6.1-T1 adds the latest render job (<c>id DESC</c>, WT49) between the
/// episode read and the asset read; 03.4.2-T1 adds this block's <c>images</c>; current story and
/// generation are read by 02.3.4-T2's shared projection. 06.2.1-T1 fills publications sequentially.
/// None replaces the episode read, so its ETag and 404 stay identical.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
/// <param name="trueCrime">The validated <c>Story:TrueCrime</c> options: the attestation version a tick must name.</param>
/// <param name="attestation">The attestation clause text of the release, for the story block's attestation member.</param>
/// <param name="logger">Reports the data-drift case, where the pointer outlives the asset row.</param>
public sealed partial class GetEpisodeByIdHandler(
    IReadDb readDb,
    StoryTrueCrimeOptions trueCrime,
    IAttestationTextProvider attestation,
    ILogger<GetEpisodeByIdHandler> logger)
    : IQueryHandler<GetEpisodeByIdQuery, ErrorOr<EpisodeDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<EpisodeDetailDto>> Handle(
        GetEpisodeByIdQuery query,
        CancellationToken cancellationToken)
    {
        // The series' topic and language decide the story block's attestation member, so they ride on
        // this read (one command, one round trip) instead of a second query per episode. `series` carries
        // the same D10 tenant filter, and `episodes.series_id` is a required FK.
        var episode = await readDb.Query<EpisodeAggregate>()
            .Where(row => row.Id == query.Id && row.Status != EpisodeStatus.Cancelled)
            .Join(
                readDb.Query<SeriesAggregate>(),
                row => row.SeriesId,
                series => series.Id,
                (row, series) => new EpisodeRow(
                    row.Id,
                    row.UserId,
                    row.SeriesId,
                    series.Topic,
                    series.Language,
                    row.Title,
                    row.Brief,
                    row.LengthTier,
                    row.Status,
                    row.FinalVideoAssetId,
                    row.Version,
                    row.CurrentStoryVersionId,
                    row.ApprovedStoryVersionId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (episode is null)
        {
            return Errors.NotFound();
        }

        // The latest render job is [RULED] to stay null until 09.6.1-T1 lands the `render_jobs` migration
        // (09 README §B10 WT2): the `RenderJob` entity does not exist in P, so the projection — and its
        // two integration tests, the latest-by-id-DESC one included — arrive with that task. Nothing is
        // read here rather than guessing at a table the database does not have.
        RenderJobSummaryDto? renderJob = null;

        FinalVideoAssetDto? finalVideo = null;

        if (episode.FinalVideoAssetId is { } assetId)
        {
            // A sequential read on the same context: the asset's tenant filter applies as above.
            var asset = await readDb.Query<Asset>()
                .Where(row => row.Id == assetId)
                .Select(row => new AssetRow(
                    row.Id,
                    row.SizeBytes,
                    row.Sha256,
                    row.ContentType,
                    row.DurationSec,
                    row.RetentionUntil))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (asset is null)
            {
                // Data drift (09.5.3 plan §Failure handling): the episode points at an asset row that is
                // gone. The page still answers 200 without the video block — never a 500 — and the log
                // carries ids only (BD16).
                LogFinalVideoAssetMissing(episode.Id, assetId);
            }
            else
            {
                finalVideo = new FinalVideoAssetDto(
                    asset.Id,
                    asset.SizeBytes,
                    asset.Sha256,
                    asset.ContentType,
                    (double?)asset.DurationSec,
                    asset.RetentionUntil);
            }
        }

        var request = new StoryReadProjection.AttestationRequest(
            episode.Id, episode.UserId, episode.Topic, episode.Language, trueCrime, attestation);
        var currentStory = await StoryReadProjection.CurrentAsync(
            readDb, episode.Id, episode.CurrentStoryVersionId, episode.Status,
            episode.ApprovedStoryVersionId, request, logger, cancellationToken).ConfigureAwait(false);

        // CreatedAt identifies the latest attempt, including queued runs whose StartedAt is null.
        // Id DESC is the deterministic tie-breaker for equal timestamps.
        var generation = await readDb.Query<GenerationRun>().AsNoTracking()
            .Where(row => row.EpisodeId == episode.Id)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => new GenerationStatusDto(
                row.Kind, row.Status, row.Stage, row.ErrorCode, row.StartedAt))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // 06.2.1-T1's third query (C7/F14, the documented exception to the two-query rule): one grouped
        // read over this episode's publications, sequential on the same context like the reads above
        // (BD16/WT46: EF forbids concurrent operations on one context). It must stay the last read and
        // must not grow a fourth round trip.
        var publications = await readDb.Query<Publication>()
            .Where(row => row.EpisodeId == episode.Id)
            .GroupBy(_ => 1)
            .Select(group => new PublicationsSummaryDto(
                group.Count(row => row.Status == PublicationStatus.Published),
                group.Count(row => row.Status == PublicationStatus.Scheduled),
                group.Max(row => row.Status == PublicationStatus.Published ? row.PublishedAt : null)))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return new EpisodeDetailDto(
            episode.Id,
            episode.SeriesId,
            episode.Title,
            episode.Brief,
            episode.LengthTier,
            episode.Status,
            EpisodeNextActions.For(episode.Status),
            ETags.Episode(episode.Version),
            renderJob,
            finalVideo,
            Publications: publications,
            Generation: generation,
            CurrentStory: currentStory);
    }

    /// <summary>
    /// The episode's own columns, the series' governance pair, and the concurrency token the ETag is
    /// built from.
    /// </summary>
    private sealed record EpisodeRow(
        Guid Id,
        Guid UserId,
        Guid SeriesId,
        Topic Topic,
        Language Language,
        string? Title,
        string Brief,
        LengthTier LengthTier,
        EpisodeStatus Status,
        Guid? FinalVideoAssetId,
        uint Version,
        Guid? CurrentStoryVersionId,
        Guid? ApprovedStoryVersionId);

    /// <summary>
    /// The asset row of <c>episodes.final_video_asset_id</c>. <c>duration_sec</c> is read as its stored
    /// <c>decimal</c> and narrowed to <c>double</c> in memory, so the projection is a plain column list
    /// the provider translates without a cast.
    /// </summary>
    private sealed record AssetRow(
        Guid Id,
        long SizeBytes,
        string Sha256,
        string ContentType,
        decimal? DurationSec,
        DateTimeOffset? RetentionUntil);

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Warning,
        Message = "episodes.detail.final-video-asset-missing episode={EpisodeId} asset={AssetId}")]
    private partial void LogFinalVideoAssetMissing(Guid episodeId, Guid assetId);
}
