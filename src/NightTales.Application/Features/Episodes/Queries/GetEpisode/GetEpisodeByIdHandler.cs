using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Assets;
using NightTales.Domain.Episodes;

// The feature namespace `…Features.Episodes` contains the aggregate's namespace segment, so the entity
// is aliased the way the series handlers alias theirs (09.4.1-T1).
using EpisodeAggregate = NightTales.Domain.Episodes.Episode;

namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// <c>GET /api/v1/episodes/{id}</c> (09.5.3 plan §4 UC3, §5): the episode page in one round trip — the
/// episode's own columns with its <c>xmin</c>, the actions the domain allows next, the final video
/// metadata the episode points at — or a 404.
/// </summary>
/// <remarks>
/// <para>
/// One projection through the tenant-filtered read model, then at most one more read for the episode's
/// final video asset, <em>sequentially</em> on the same <see cref="IReadDb"/> ([RULED — README §B10
/// WT46]: EF forbids concurrent operations on one context, so there is no <c>Task.WhenAll</c> here), and
/// no <c>Include</c>, no N+1 and no third query (BD16). Another user's episode is simply not in the
/// projection, so it, an unknown id and a cancelled row all answer the same 404 (09 README D10, F14).
/// </para>
/// <para>
/// The ETag is built from the row's <c>xmin</c>, which the D8 convention maps to
/// <c>AggregateRoot.Version</c>: a read returns the value that is current <em>now</em>, which is
/// exactly what <c>If-Match</c> on the next write must compare against (09 README D15).
/// </para>
/// <para>
/// Extension points, one route and one projection each — never a second handler for this resource
/// (09.5.3 plan §Steps 6): 09.6.1-T1 adds the latest render job (<c>id DESC</c>, WT49) between the
/// episode read and the asset read; 03.4.2-T1 adds this block's <c>images</c>; 02.3.4-T2 adds
/// <c>currentStory</c>/<c>generation</c>; 06.2.1-T1 fills <c>publications</c> from its own approved
/// third query. None of them replaces the episode read, so the ETag and the 404 stay identical.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
/// <param name="logger">Reports the data-drift case, where the pointer outlives the asset row.</param>
public sealed partial class GetEpisodeByIdHandler(IReadDb readDb, ILogger<GetEpisodeByIdHandler> logger)
    : IQueryHandler<GetEpisodeByIdQuery, ErrorOr<EpisodeDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<EpisodeDetailDto>> Handle(
        GetEpisodeByIdQuery query,
        CancellationToken cancellationToken)
    {
        var episode = await readDb.Query<EpisodeAggregate>()
            .Where(row => row.Id == query.Id && row.Status != EpisodeStatus.Cancelled)
            .Select(row => new EpisodeRow(
                row.Id,
                row.SeriesId,
                row.Title,
                row.Brief,
                row.LengthTier,
                row.Status,
                row.FinalVideoAssetId,
                row.Version))
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
            // The second and last read, on the same context: `assets` is IUserOwned, so the tenant filter
            // narrows it exactly as the episode read was narrowed.
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
            Publications: null);
    }

    /// <summary>The episode's own columns, including the concurrency token the ETag is built from.</summary>
    private sealed record EpisodeRow(
        Guid Id,
        Guid SeriesId,
        string? Title,
        string Brief,
        LengthTier LengthTier,
        EpisodeStatus Status,
        Guid? FinalVideoAssetId,
        uint Version);

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
