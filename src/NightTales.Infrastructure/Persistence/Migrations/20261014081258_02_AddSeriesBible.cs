using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_AddSeriesBible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "series_bibles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    proposed_content = table.Column<string>(type: "jsonb", nullable: true),
                    proposed_from_episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series_bibles", x => x.id);
                    table.ForeignKey(
                        name: "fk_series_bibles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_bibles_episodes_proposed_from_episode_id",
                        column: x => x.proposed_from_episode_id,
                        principalTable: "episodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_series_bibles_series_series_id_user_id",
                        columns: x => new { x.series_id, x.user_id },
                        principalTable: "series",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_series_bibles_proposed_from_episode_id",
                table: "series_bibles",
                column: "proposed_from_episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_bibles_series_id_user_id",
                table: "series_bibles",
                columns: new[] { "series_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_series_bibles_user_id",
                table: "series_bibles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "UX_series_bibles_series_id",
                table: "series_bibles",
                column: "series_id",
                unique: true);

            // 02.6.3 plan §3.3: every series that already exists gets its one empty `bible.v1` row, so the row
            // the create path now writes is also there for the series that predate this migration. The document
            // is the canonical JSON of `SeriesBibleContent.Empty` through `BibleJson` (the same bytes
            // `BibleJsonTests` pins and the seed's own `series_bibles` row carries), and `version` is the
            // factory's `InitialVersion`. `WHERE NOT EXISTS` makes the step idempotent: a series that already
            // has a row — written by the create path, or by an earlier run of this very statement — is skipped
            // instead of failing on the unique `series_id` index. Expand-only: `Down` drops the whole table, and
            // a greenfield database has no `series` row to backfill.
            migrationBuilder.Sql(
                """
                INSERT INTO series_bibles (id, user_id, series_id, version, content, created_at, updated_at)
                SELECT gen_random_uuid(), s.user_id, s.id, 1,
                       jsonb_build_object('schema_version','bible.v1','narrator',
                         jsonb_build_object('name', NULL, 'persona', '', 'voice_notes', ''),
                         'recurring_characters','[]'::jsonb,'places','[]'::jsonb,'rules','[]'::jsonb,
                         'episode_summaries','[]'::jsonb),
                       now(), now()
                FROM series s
                WHERE NOT EXISTS (SELECT 1 FROM series_bibles b WHERE b.series_id = s.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "series_bibles");
        }
    }
}
