using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering.Music;

/// <summary>
/// One track of the global music catalog (05 README D8, P5; table <c>music_tracks</c>). It is deliberately
/// not <see cref="IUserOwned"/>: the catalog is identical for every tenant, so 09 README D10's tenant filter
/// never applies and no <c>IgnoreQueryFilters</c> entry is needed — a filter here would silently empty the
/// selector's candidate set (<c>music_none</c>).
/// </summary>
/// <remarks>
/// The <c>MusicTrackSeeder</c> upserts rows by <see cref="TrackId"/> through <see cref="Create"/> and
/// <see cref="UpdateFromCatalog"/>, and never deletes one. The class shape (private setters, a factory) is the
/// one <see cref="NightTales.Domain.Catalog.Voice"/> uses, so EF can track and update a row in place.
/// </remarks>
public sealed class MusicTrack : Entity, IAuditable
{
    /// <summary>The decimal places of <c>duration_sec</c> (<c>numeric(7,1)</c>).</summary>
    public const int DurationScale = 1;

    /// <summary>
    /// The decimal places of <c>loudness_lufs</c> (<c>numeric(5,2)</c>). The measured values carry two places and
    /// the D8 gain check recomputes from the stored loudness, so a coarser column would round a gain out of
    /// agreement with its own formula.
    /// </summary>
    public const int LoudnessScale = 2;

    private MusicTrack()
    {
    }

    /// <summary>The natural key (<c>id</c> in the file); it equals the file stem.</summary>
    public string TrackId { get; private set; } = string.Empty;

    /// <summary>The audio file name, <c>.mp3</c> or <c>.m4a</c>.</summary>
    public string FileName { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Artist { get; private set; } = string.Empty;

    public string SourceUrl { get; private set; } = string.Empty;

    public MusicLicense License { get; private set; }

    /// <summary>The attribution text; non-empty exactly for <see cref="MusicLicense.CcBy"/>.</summary>
    public string? Attribution { get; private set; }

    public MusicPlatformScope PlatformScope { get; private set; }

    /// <summary>One to three moods of <see cref="MusicTrackRules.Moods"/>, in file order.</summary>
    public List<string> Moods { get; private set; } = [];

    public decimal DurationSec { get; private set; }

    public decimal LoudnessLufs { get; private set; }

    /// <summary>The gain the render applies, <see cref="MusicTrackRules.ComputeBgmVolume"/> of the loudness.</summary>
    public decimal BgmVolume { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>The object-store key, <c>music/&lt;file&gt;</c>.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>The revision of the catalog file that last wrote the row.</summary>
    public int CatalogRevision { get; private set; }

    /// <summary>Whether the selector may pick the track; a track removed from the file stays, inactive.</summary>
    public bool Active { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a row; the id is a UUIDv7. The caller validates with <see cref="MusicTrackRules"/> first.</summary>
    public static MusicTrack Create(
        string trackId,
        string fileName,
        string title,
        string artist,
        string sourceUrl,
        MusicLicense license,
        string? attribution,
        MusicPlatformScope platformScope,
        IReadOnlyList<string> moods,
        decimal durationSec,
        decimal loudnessLufs,
        decimal bgmVolume,
        string sha256,
        string storageKey,
        int catalogRevision,
        bool active = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(moods);

        var track = new MusicTrack { Id = Guid.CreateVersion7(), TrackId = trackId };
        track.Apply(
            fileName, title, artist, sourceUrl, license, attribution, platformScope, moods, durationSec,
            loudnessLufs, bgmVolume, sha256, storageKey, catalogRevision, active);

        return track;
    }

    /// <summary>Rewrites every catalog-owned column of an existing row; the id and <see cref="TrackId"/> stay.</summary>
    public void UpdateFromCatalog(
        string fileName,
        string title,
        string artist,
        string sourceUrl,
        MusicLicense license,
        string? attribution,
        MusicPlatformScope platformScope,
        IReadOnlyList<string> moods,
        decimal durationSec,
        decimal loudnessLufs,
        decimal bgmVolume,
        string sha256,
        string storageKey,
        int catalogRevision,
        bool active)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(moods);

        Apply(
            fileName, title, artist, sourceUrl, license, attribution, platformScope, moods, durationSec,
            loudnessLufs, bgmVolume, sha256, storageKey, catalogRevision, active);
    }

    /// <summary>
    /// Marks the track inactive because the current catalog file no longer lists it. The row is kept so an old
    /// <c>params.music.track_id</c> still resolves to its <see cref="PlatformScope"/>.
    /// </summary>
    /// <returns><c>true</c> when the row changed.</returns>
    public bool Deactivate()
    {
        if (!Active)
        {
            return false;
        }

        Active = false;

        return true;
    }

    private void Apply(
        string fileName,
        string title,
        string artist,
        string sourceUrl,
        MusicLicense license,
        string? attribution,
        MusicPlatformScope platformScope,
        IReadOnlyList<string> moods,
        decimal durationSec,
        decimal loudnessLufs,
        decimal bgmVolume,
        string sha256,
        string storageKey,
        int catalogRevision,
        bool active)
    {
        FileName = fileName;
        Title = title;
        Artist = artist;
        SourceUrl = sourceUrl;
        License = license;
        Attribution = string.IsNullOrWhiteSpace(attribution) ? null : attribution;
        PlatformScope = platformScope;
        Moods = [.. moods];

        // The columns are numeric(7,1) / numeric(5,2): round to the stored scale like PostgreSQL does, so a
        // re-seed compares the same values the database holds and stays a no-op.
        DurationSec = Math.Round(durationSec, DurationScale, MidpointRounding.AwayFromZero);
        LoudnessLufs = Math.Round(loudnessLufs, LoudnessScale, MidpointRounding.AwayFromZero);
        BgmVolume = bgmVolume;
        Sha256 = sha256;
        StorageKey = storageKey;
        CatalogRevision = catalogRevision;
        Active = active;
    }
}
