using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_AddSourceCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    normalized_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    registrable_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    site_name = table.Column<string>(type: "text", nullable: false),
                    rank = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_candidates", x => x.id);
                    table.CheckConstraint("ck_source_candidates_added", "(\"status\" = 'added') = (\"source_id\" IS NOT NULL)");
                    table.CheckConstraint("ck_source_candidates_rank", "\"rank\" BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_source_candidates_status", "\"status\" IN ('added', 'dismissed', 'proposed')");
                    table.ForeignKey(
                        name: "fk_source_candidates_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_source_candidates_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_source_candidates_generation_runs_generation_run_id_user_id",
                        columns: x => new { x.generation_run_id, x.user_id },
                        principalTable: "generation_runs",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_source_candidates_story_sources_source_id_user_id",
                        columns: x => new { x.source_id, x.user_id },
                        principalTable: "story_sources",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_candidates_episode_id_user_id",
                table: "source_candidates",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_source_candidates_episode_status_rank",
                table: "source_candidates",
                columns: new[] { "episode_id", "status", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_source_candidates_generation_run_id_user_id",
                table: "source_candidates",
                columns: new[] { "generation_run_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_source_candidates_source_id_user_id",
                table: "source_candidates",
                columns: new[] { "source_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_source_candidates_user_id",
                table: "source_candidates",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_source_candidates_episode_url",
                table: "source_candidates",
                columns: new[] { "episode_id", "normalized_url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_candidates");
        }
    }
}
