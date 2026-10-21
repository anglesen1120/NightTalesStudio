using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _09_RenderJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "render_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    story_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    percent = table.Column<short>(type: "smallint", nullable: false),
                    adapter_task_id = table.Column<string>(type: "text", nullable: true),
                    worker_node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    quality_report = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    next_poll_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    images_needed = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_render_jobs", x => x.id);
                    table.CheckConstraint("ck_render_jobs_status", "\"status\" IN ('cancelled', 'failed', 'ingesting', 'queued', 'running', 'submitted', 'succeeded')");
                    table.ForeignKey(
                        name: "fk_render_jobs_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_render_jobs_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_render_jobs_story_versions_story_version_id_user_id",
                        columns: x => new { x.story_version_id, x.user_id },
                        principalTable: "story_versions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_render_jobs_episode_id_user_id",
                table: "render_jobs",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_render_jobs_status_next_poll_at",
                table: "render_jobs",
                columns: new[] { "status", "next_poll_at" },
                filter: "\"status\" IN ('submitted', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_render_jobs_story_version_id_user_id",
                table: "render_jobs",
                columns: new[] { "story_version_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_render_jobs_user_id_episode_id",
                table: "render_jobs",
                columns: new[] { "user_id", "episode_id" });

            migrationBuilder.CreateIndex(
                name: "ux_render_jobs_active_episode",
                table: "render_jobs",
                column: "episode_id",
                unique: true,
                filter: "\"status\" IN ('queued', 'submitted', 'running', 'ingesting')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "render_jobs");
        }
    }
}
