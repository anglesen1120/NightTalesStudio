using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Assets;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Inserts real <c>episodes</c> rows (09 README D13, 09 W3 CR-41) for an existing series. The status is
/// a parameter, because the state tests of 09.5.1-T1 and 02 drive episodes from every state.
/// </summary>
/// <remarks>
/// The row goes through <see cref="AppDbContext"/> like production code, so the FKs, the enum wire
/// values and the audit stamps are exactly the ones a real request writes.
/// </remarks>
public sealed class EpisodeBuilder(TestDatabase database, TestCurrentUser currentUser, IClock? clock = null)
{
    /// <summary>The default brief the builder writes.</summary>
    public const string DefaultBrief = "Một tập thử nghiệm cho kiểm thử.";

    /// <summary>
    /// Inserts one episode of <paramref name="seriesId"/>. <paramref name="userId"/> must own the series;
    /// <paramref name="createdAt"/> defaults to the interceptor stamp.
    /// </summary>
    public async Task<Episode> CreateAsync(
        Guid userId,
        Guid seriesId,
        string brief = DefaultBrief,
        string? title = null,
        LengthTier lengthTier = LengthTier.Standard,
        EpisodeStatus status = EpisodeStatus.Draft,
        DateTimeOffset? createdAt = null,
        CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();

        await using var context = database.CreateAppDbContext(currentUser, clock);

        var episode = ShellInstance.Create<Episode>();
        context.Episodes.Add(episode);

        ShellInstance.Set(context, episode, nameof(Episode.Id), id);
        ShellInstance.Set(context, episode, nameof(Episode.UserId), userId);
        ShellInstance.Set(context, episode, nameof(Episode.SeriesId), seriesId);
        ShellInstance.Set(context, episode, nameof(Episode.Title), title);
        ShellInstance.Set(context, episode, nameof(Episode.Brief), brief);
        ShellInstance.Set(context, episode, nameof(Episode.LengthTier), lengthTier);
        ShellInstance.Set(context, episode, nameof(Episode.Status), status);

        if (createdAt is { } stamp)
        {
            ShellInstance.Set(context, episode, nameof(Episode.CreatedAt), stamp);
            ShellInstance.Set(context, episode, nameof(Episode.UpdatedAt), stamp);
        }

        await context.SaveChangesAsync(ct);

        return episode;
    }

    /// <summary>The content type every builder asset carries.</summary>
    public const string DefaultContentType = "video/mp4";

    /// <summary>The size every builder asset carries unless the caller sets one.</summary>
    public const long DefaultSizeBytes = 1_024L;

    /// <summary>The lowercase SHA-256 hex every builder asset carries unless the caller sets one.</summary>
    public static readonly string DefaultSha256 = new('a', 64);

    /// <summary>
    /// Inserts one <c>assets</c> row of <paramref name="episodeId"/>. 09.7.x owns the asset behaviour
    /// (<c>ScheduleRetention</c>, <c>MarkDeleted</c>), so the row is materialized the way
    /// <see cref="ShellInstance"/> does it for a shell: real columns, real FK to the episode and real
    /// audit stamps, through the same <see cref="AppDbContext"/> production code uses.
    /// </summary>
    /// <remarks>
    /// The storage key is unique per call (it embeds the id) because <c>assets.storage_key</c> carries a
    /// unique index, and the SHA-256 is a fixed 64-character hex value because the column is
    /// <c>char(64)</c>.
    /// </remarks>
    /// <param name="userId">The owner; must own the episode (the FK is the same-owner alternate key).</param>
    /// <param name="episodeId">The episode the asset belongs to.</param>
    /// <param name="kind">The asset kind.</param>
    /// <param name="retentionUntil">A pre-set retention stamp, or <see langword="null"/> for none.</param>
    /// <param name="deletedAt">A tombstone stamp, or <see langword="null"/> for a live asset.</param>
    /// <param name="sizeBytes">The stored size the detail block reports.</param>
    /// <param name="durationSec">The media duration, or <see langword="null"/> (09.5.3-T1's video block).</param>
    /// <param name="sha256">The content hash; a 64-character hex value.</param>
    /// <param name="ct">Cancels the insert.</param>
    public async Task<Asset> CreateAssetAsync(
        Guid userId,
        Guid episodeId,
        AssetKind kind = AssetKind.Video,
        DateTimeOffset? retentionUntil = null,
        DateTimeOffset? deletedAt = null,
        long sizeBytes = DefaultSizeBytes,
        decimal? durationSec = null,
        string? sha256 = null,
        CancellationToken ct = default)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        var asset = ShellInstance.Create<Asset>();
        context.Assets.Add(asset);

        ShellInstance.Set(context, asset, nameof(Asset.Id), Guid.CreateVersion7());
        ShellInstance.Set(context, asset, nameof(Asset.UserId), userId);
        ShellInstance.Set(context, asset, nameof(Asset.EpisodeId), episodeId);
        ShellInstance.Set(context, asset, nameof(Asset.Kind), kind);
        ShellInstance.Set(
            context,
            asset,
            nameof(Asset.StorageKey),
            $"media/{userId}/{episodeId}/{Guid.CreateVersion7():N}.mp4");
        ShellInstance.Set(context, asset, nameof(Asset.ContentType), DefaultContentType);
        ShellInstance.Set(context, asset, nameof(Asset.SizeBytes), sizeBytes);
        ShellInstance.Set(context, asset, nameof(Asset.DurationSec), durationSec);
        ShellInstance.Set(context, asset, nameof(Asset.Sha256), sha256 ?? DefaultSha256);
        ShellInstance.Set(context, asset, nameof(Asset.RetentionUntil), retentionUntil);
        ShellInstance.Set(context, asset, nameof(Asset.DeletedAt), deletedAt);

        await context.SaveChangesAsync(ct);

        return asset;
    }

    /// <summary>
    /// Points <c>episodes.final_video_asset_id</c> at an existing asset of the same episode — what the
    /// ingest writes when a render succeeds (09.6.3-T2), and what the detail route reads its video block
    /// from (09.5.3-T1). The asset must be inserted first: the column is a same-owner FK.
    /// </summary>
    /// <remarks>
    /// The builder's current user must be <paramref name="userId"/>: this method <em>queries</em> the
    /// episode, so the D10 tenant filter applies, unlike <see cref="CreateAsync"/>, which only inserts.
    /// </remarks>
    /// <param name="userId">The owner of the episode and of the asset.</param>
    /// <param name="episodeId">The episode to update.</param>
    /// <param name="assetId">The asset the episode's video is.</param>
    /// <param name="ct">Cancels the update.</param>
    public async Task AttachFinalVideoAsync(
        Guid userId,
        Guid episodeId,
        Guid assetId,
        CancellationToken ct = default)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        var episode = await context.Episodes.SingleAsync(row => row.Id == episodeId, ct);
        ShellInstance.Set(context, episode, nameof(Episode.FinalVideoAssetId), assetId);

        await context.SaveChangesAsync(ct);
    }
}
