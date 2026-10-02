using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _05_MusicTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "music_tracks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    track_id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    artist = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: false),
                    license = table.Column<string>(type: "text", nullable: false),
                    attribution = table.Column<string>(type: "text", nullable: true),
                    platform_scope = table.Column<string>(type: "text", nullable: false),
                    moods = table.Column<List<string>>(type: "text[]", nullable: false),
                    duration_sec = table.Column<decimal>(type: "numeric(7,1)", nullable: false),
                    loudness_lufs = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    bgm_volume = table.Column<decimal>(type: "numeric(4,3)", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    catalog_revision = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_music_tracks", x => x.id);
                    table.CheckConstraint("ck_music_tracks_attribution", "(\"license\" = 'CC-BY-4.0') = (\"attribution\" IS NOT NULL AND btrim(\"attribution\") <> '')");
                    table.CheckConstraint("ck_music_tracks_bgm_volume", "\"bgm_volume\" BETWEEN 0.03 AND 0.40 AND \"bgm_volume\" = round(least(0.40, greatest(0.03, power(10::numeric, (-34 - \"loudness_lufs\") / 20))), 3)");
                    table.CheckConstraint("ck_music_tracks_duration_sec", "\"duration_sec\" >= 60");
                    table.CheckConstraint("ck_music_tracks_file_name", "\"file_name\" ~ '\\.(mp3|m4a)$' AND regexp_replace(\"file_name\", '\\.(mp3|m4a)$', '') = \"track_id\"");
                    table.CheckConstraint("ck_music_tracks_license", "\"license\" IN ('CC0-1.0', 'CC-BY-4.0', 'YouTube-Audio-Library')");
                    table.CheckConstraint("ck_music_tracks_moods", "\"moods\" <@ ARRAY['dark_ambient','suspense','temple','meditation','noir','calm']::text[] AND cardinality(\"moods\") BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_music_tracks_platform_scope", "\"platform_scope\" IN ('any', 'youtube') AND (\"license\" <> 'YouTube-Audio-Library' OR \"platform_scope\" = 'youtube')");
                    table.CheckConstraint("ck_music_tracks_sha256", "\"sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_music_tracks_track_id", "\"track_id\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND char_length(\"track_id\") <= 40");
                });

            migrationBuilder.CreateIndex(
                name: "ux_music_tracks_track_id",
                table: "music_tracks",
                column: "track_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "music_tracks");
        }
    }
}
