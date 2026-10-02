using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Rendering.Music;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>music_tracks</c> table (05 README D8/P5/CR-2, owner 05.3.2-T3): the global music catalog the
/// <c>MusicTrackSeeder</c> upserts from <c>seed/music-tracks.v1.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>user_id</c>, no tenant filter and no <c>IgnoreQueryFilters</c> allow-list entry: the catalog
/// is identical for every tenant (09 README D10). A filter here would empty the selector's candidate set and
/// silently turn every render into <c>music_none</c>.
/// </para>
/// <para>
/// <c>license</c> and <c>platform_scope</c> are stored as the file's own values (<c>CC-BY-4.0</c>,
/// <c>youtube</c>), not the snake_case enum names of <c>HasEnumCheck</c>, because the stored text is the
/// frozen <c>params.music</c> / attribution vocabulary. Their <c>CHECK</c>s are therefore written out here.
/// The database repeats every D8 rule of <see cref="MusicTrackRules"/> as the last line of defence.
/// </para>
/// <para>
/// <c>loudness_lufs</c> is <c>numeric(5,2)</c>, not the task table's <c>(5,1)</c>: the committed catalog
/// measures loudness to two places and the gain CHECK recomputes from the stored value, so one place rounds
/// 16 of the 30 tracks out of agreement with their own <c>bgm_volume</c>.
/// </para>
/// </remarks>
internal sealed class MusicTrackConfiguration : IEntityTypeConfiguration<MusicTrack>
{
    public void Configure(EntityTypeBuilder<MusicTrack> builder)
    {
        builder.ToTable("music_tracks");
        builder.HasKey(track => track.Id);

        // UUIDv7 comes from MusicTrack.Create, never from the database (09 README D3/D8).
        builder.Property(track => track.Id).ValueGeneratedNever();

        builder.Property(track => track.TrackId).IsRequired();
        builder.Property(track => track.FileName).IsRequired();
        builder.Property(track => track.Title).IsRequired();
        builder.Property(track => track.Artist).IsRequired();
        builder.Property(track => track.SourceUrl).IsRequired();

        builder.Property(track => track.License)
            .HasConversion(
                license => MusicTrackRules.LicenseName(license),
                name => ParseLicense(name))
            .HasColumnType("text")
            .IsRequired();
        builder.Property(track => track.Attribution);
        builder.Property(track => track.PlatformScope)
            .HasConversion(
                scope => MusicTrackRules.ScopeName(scope),
                name => ParseScope(name))
            .HasColumnType("text")
            .IsRequired();

        builder.Property(track => track.Moods).HasColumnType("text[]").IsRequired();
        builder.Property(track => track.DurationSec).HasColumnType("numeric(7,1)");
        builder.Property(track => track.LoudnessLufs).HasColumnType("numeric(5,2)");
        builder.Property(track => track.BgmVolume).HasColumnType("numeric(4,3)");
        builder.Property(track => track.Sha256).HasColumnType("char(64)").IsRequired();
        builder.Property(track => track.StorageKey).IsRequired();
        builder.Property(track => track.CatalogRevision).IsRequired();
        builder.Property(track => track.Active).HasDefaultValue(true);

        builder.Property(track => track.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(track => track.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(track => track.TrackId)
            .HasDatabaseName("ux_music_tracks_track_id")
            .IsUnique();

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_music_tracks_track_id",
                $"\"track_id\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(\"track_id\") <= {MusicTrackRules.TrackIdMaxLength}");
            table.HasCheckConstraint(
                "ck_music_tracks_file_name",
                "\"file_name\" ~ '\\.(mp3|m4a)$' AND regexp_replace(\"file_name\", '\\.(mp3|m4a)$', '') = \"track_id\"");
            table.HasCheckConstraint(
                "ck_music_tracks_license",
                "\"license\" IN ('CC0-1.0', 'CC-BY-4.0', 'YouTube-Audio-Library')");
            table.HasCheckConstraint(
                "ck_music_tracks_attribution",
                "(\"license\" = 'CC-BY-4.0') = (\"attribution\" IS NOT NULL AND btrim(\"attribution\", E' \\t\\r\\n') <> '')");
            table.HasCheckConstraint(
                "ck_music_tracks_platform_scope",
                "\"platform_scope\" IN ('any', 'youtube') AND "
                + "((\"license\" = 'YouTube-Audio-Library') = (\"platform_scope\" = 'youtube'))");
            table.HasCheckConstraint(
                "ck_music_tracks_moods",
                "\"moods\" <@ ARRAY['dark_ambient','suspense','temple','meditation','noir','calm']::text[] "
                + $"AND cardinality(\"moods\") BETWEEN {MusicTrackRules.MoodsMin} AND {MusicTrackRules.MoodsMax}");
            table.HasCheckConstraint(
                "ck_music_tracks_duration_sec",
                $"\"duration_sec\" >= {MusicTrackRules.MinDurationSec} AND \"duration_sec\" <= {MusicTrackRules.MaxDurationSec}");
            table.HasCheckConstraint(
                "ck_music_tracks_bgm_volume",
                "\"bgm_volume\" BETWEEN 0.03 AND 0.40 AND \"bgm_volume\" = round("
                + "least(0.40, greatest(0.03, power(10::numeric, (-34 - \"loudness_lufs\") / 20))), 3)");
            table.HasCheckConstraint("ck_music_tracks_sha256", "\"sha256\" ~ '^[0-9a-f]{64}$'");
        });
    }

    private static MusicLicense ParseLicense(string name) =>
        MusicTrackRules.TryParseLicense(name, out var license)
            ? license
            : throw new InvalidOperationException($"Unknown music licence '{name}' in music_tracks.license.");

    private static MusicPlatformScope ParseScope(string name) =>
        MusicTrackRules.TryParseScope(name, out var scope)
            ? scope
            : throw new InvalidOperationException($"Unknown platform scope '{name}' in music_tracks.platform_scope.");
}
