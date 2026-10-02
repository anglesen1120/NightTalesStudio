using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Rendering.Music;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Upserts the <c>music_tracks</c> catalog (05 README D8, 05.3.2-T3; 09.2.5 plan §3.1 pattern) by natural key
/// <see cref="MusicTrack.TrackId"/> from the tracked <c>seed/music-tracks.v1.json</c> (Development auto-seed,
/// <c>nts-seed</c> elsewhere; 05 §10 T85). A present row is updated in every catalog column, a new one is inserted with a UUIDv7, a row the
/// file no longer lists is marked <c>active = false</c>, and nothing is ever deleted, so an old
/// <c>params.music.track_id</c> still resolves to its <c>platform_scope</c>.
/// </summary>
/// <remarks>
/// A failing file is rejected whole (<c>seed_music_invalid:&lt;track id or -&gt;:&lt;code&gt;</c>) before a row is
/// written, and so is <c>revision &lt; 1</c> outside Development; in Development that revision only warns. A file
/// that is absent is logged as an error and leaves the table untouched, so the host still starts and the render
/// path degrades to no music (<c>music_none</c>, D10).
/// </remarks>
public sealed partial class MusicTrackSeeder(
    IAppDbContext db,
    IOptions<SeedOptions> options,
    IHostEnvironment environment,
    ILogger<MusicTrackSeeder> logger) : ISeeder
{
    private const string NoTrackId = "-";
    private const long MaxCatalogBytes = 1024 * 1024;

    /// <inheritdoc />
    public string Name => "music_tracks";

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        var file = LoadFile();
        if (file is null)
        {
            return new SeedReport(Name, 0, 0, 0);
        }

        try
        {
            return await ApplyAsync(file, ct);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            // The Api and the Worker may seed at once: the loser of the track_id race re-reads the committed
            // row and applies it as an update.
            LogConcurrentInsertRetry();
            SeedDbContext.ResetTracking(db);

            return await ApplyOnceAsync(file, ct);
        }
        catch (DbUpdateException)
        {
            throw SeedFailure("db_rejected", trackId: null);
        }
    }

    private async ValueTask<SeedReport> ApplyOnceAsync(MusicCatalogFile file, CancellationToken ct)
    {
        try
        {
            return await ApplyAsync(file, ct);
        }
        catch (DbUpdateException)
        {
            throw SeedFailure("db_rejected", trackId: null);
        }
    }

    private MusicCatalogFile? LoadFile()
    {
        var catalogFile = options.Value.MusicTracksFile;
        var path = SeedFiles.Resolve(catalogFile, environment);
        if (path is null)
        {
            LogCatalogMissing(catalogFile);

            return null;
        }

        byte[] bytes;
        try
        {
            // A catalog is a few dozen tracks; refuse anything large before it is read into memory.
            if (new FileInfo(path).Length > MaxCatalogBytes)
            {
                LogCatalogUnreadable(path, "TooLarge");

                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCatalogUnreadable(path, exception.GetType().Name);

            return null;
        }

        var loaded = MusicCatalogFileReader.Load(bytes, allowNonPositiveRevision: environment.IsDevelopment());
        if (loaded.IsError)
        {
            throw SeedFailure(loaded.FirstError);
        }

        var file = loaded.Value;
        if (file.Revision < 1)
        {
            // Only reachable in Development: the reader refuses it everywhere else.
            LogRevisionBelowOne(file.Revision);
        }

        return file;
    }

    private async ValueTask<SeedReport> ApplyAsync(MusicCatalogFile file, CancellationToken ct)
    {
        var existing = await db.MusicTracks.ToDictionaryAsync(track => track.TrackId, StringComparer.Ordinal, ct);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var inserted = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var track in file.Tracks)
        {
            listed.Add(track.TrackId);
            var storageKey = $"music/{track.FileName}";

            if (!existing.TryGetValue(track.TrackId, out var row))
            {
                db.MusicTracks.Add(MusicTrack.Create(
                    track.TrackId, track.FileName, track.Title, track.Artist, track.SourceUrl, track.License,
                    track.Attribution, track.PlatformScope, track.Moods, track.DurationSec, track.LoudnessLufs,
                    track.BgmVolume, track.Sha256, storageKey, file.Revision, track.Active));
                inserted++;
                continue;
            }

            var current = MusicTrack.Create(
                track.TrackId, track.FileName, track.Title, track.Artist, track.SourceUrl, track.License,
                track.Attribution, track.PlatformScope, track.Moods, track.DurationSec, track.LoudnessLufs,
                track.BgmVolume, track.Sha256, storageKey, file.Revision, track.Active);
            if (SameCatalogValues(row, current))
            {
                skipped++;
                continue;
            }

            row.UpdateFromCatalog(
                track.FileName, track.Title, track.Artist, track.SourceUrl, track.License, track.Attribution,
                track.PlatformScope, track.Moods, track.DurationSec, track.LoudnessLufs, track.BgmVolume,
                track.Sha256, storageKey, file.Revision, track.Active);
            updated++;
        }

        // A track the file no longer lists stays (an old params.music.track_id still resolves) but is not offered.
        foreach (var (trackId, row) in existing)
        {
            if (!listed.Contains(trackId) && row.Deactivate())
            {
                updated++;
            }
        }

        if (inserted + updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SeedReport(Name, inserted, updated, skipped);
    }

    /// <summary>
    /// Compares every catalog-owned column. <paramref name="current"/> was built through the same
    /// <see cref="MusicTrack.Create"/> path, so its decimals are already rounded to the column scale.
    /// </summary>
    private static bool SameCatalogValues(MusicTrack row, MusicTrack current) =>
        row.FileName == current.FileName
        && row.Title == current.Title
        && row.Artist == current.Artist
        && row.SourceUrl == current.SourceUrl
        && row.License == current.License
        && row.Attribution == current.Attribution
        && row.PlatformScope == current.PlatformScope
        && row.Moods.SequenceEqual(current.Moods, StringComparer.Ordinal)
        && row.DurationSec == current.DurationSec
        && row.LoudnessLufs == current.LoudnessLufs
        && row.BgmVolume == current.BgmVolume
        && row.Sha256 == current.Sha256
        && row.StorageKey == current.StorageKey
        && row.CatalogRevision == current.CatalogRevision
        && row.Active == current.Active;

    private static SeedValidationException SeedFailure(Error error) =>
        SeedFailure(error.Code, TrackIdOf(error));

    private static SeedValidationException SeedFailure(string code, string? trackId) =>
        new($"seed_music_invalid:{trackId ?? NoTrackId}:{code}");

    private static string? TrackIdOf(Error error) =>
        error.Metadata is not null
        && error.Metadata.TryGetValue(MusicTrackRules.TrackIdMetadataKey, out var trackId)
        && trackId is string id
            ? id
            : null;

    [LoggerMessage(EventId = 2340, Level = LogLevel.Error, Message = "seed.music_tracks.catalog-missing path={Path}")]
    private partial void LogCatalogMissing(string path);

    [LoggerMessage(EventId = 2341, Level = LogLevel.Error, Message = "seed.music_tracks.catalog-unreadable path={Path} error={Error}")]
    private partial void LogCatalogUnreadable(string path, string error);

    [LoggerMessage(EventId = 2343, Level = LogLevel.Warning, Message = "seed.music_tracks.revision-below-1 revision={Revision}")]
    private partial void LogRevisionBelowOne(int revision);

    [LoggerMessage(EventId = 2342, Level = LogLevel.Information, Message = "seed.music_tracks.concurrent-insert-retry")]
    private partial void LogConcurrentInsertRetry();
}
