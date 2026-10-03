using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_SourcesAndFetchTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_creations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_creations", x => x.id);
                    table.ForeignKey(
                        name: "fk_source_creations_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "story_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    normalized_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    registrable_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    note_text = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    content_sha256 = table.Column<string>(type: "char(64)", nullable: true),
                    extracted_text_key = table.Column<string>(type: "text", nullable: true),
                    text_chars = table.Column<int>(type: "integer", nullable: true),
                    final_url = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_sources", x => x.id);
                    table.UniqueConstraint("ak_story_sources_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_story_sources_kind", "\"kind\" IN ('url', 'user_note')");
                    table.CheckConstraint("ck_story_sources_note", "\"kind\" <> 'user_note' OR (\"url\" IS NULL AND \"note_text\" IS NOT NULL)");
                    table.CheckConstraint("ck_story_sources_note_len", "\"note_text\" IS NULL OR char_length(\"note_text\") <= 3000");
                    table.CheckConstraint("ck_story_sources_sha256", "\"content_sha256\" IS NULL OR \"content_sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_story_sources_status", "\"status\" IN ('failed', 'fetched', 'pending', 'rejected')");
                    table.CheckConstraint("ck_story_sources_text_chars", "\"text_chars\" IS NULL OR (\"text_chars\" >= 0 AND \"text_chars\" <= 300000)");
                    table.CheckConstraint("ck_story_sources_url", "\"kind\" <> 'url' OR (\"url\" IS NOT NULL AND \"normalized_url\" IS NOT NULL AND \"registrable_domain\" IS NOT NULL AND \"note_text\" IS NULL)");
                    table.CheckConstraint("ck_story_sources_url_len", "\"normalized_url\" IS NULL OR char_length(\"normalized_url\") <= 2048");
                    table.ForeignKey(
                        name: "fk_story_sources_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_story_sources_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fetch_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    generation_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    registrable_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    not_before = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    leased_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    leased_by = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fetch_tasks", x => x.id);
                    table.CheckConstraint("ck_fetch_tasks_attempts", "\"attempts\" >= 0");
                    table.CheckConstraint("ck_fetch_tasks_fetch_source", "\"kind\" <> 'fetch_source' OR (\"source_id\" IS NOT NULL AND \"url\" IS NOT NULL AND \"registrable_domain\" IS NOT NULL)");
                    table.CheckConstraint("ck_fetch_tasks_kind", "\"kind\" IN ('fetch_source', 'find_sources')");
                    table.CheckConstraint("ck_fetch_tasks_leased", "\"status\" <> 'leased' OR (\"lease_until\" IS NOT NULL AND \"leased_by\" IS NOT NULL)");
                    table.CheckConstraint("ck_fetch_tasks_status", "\"status\" IN ('cancelled', 'done', 'failed', 'leased', 'queued')");
                    table.ForeignKey(
                        name: "fk_fetch_tasks_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fetch_tasks_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_fetch_tasks_story_sources_source_id_user_id",
                        columns: x => new { x.source_id, x.user_id },
                        principalTable: "story_sources",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fetch_tasks_episode_id_user_id",
                table: "fetch_tasks",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fetch_tasks_leased_domain",
                table: "fetch_tasks",
                columns: new[] { "status", "registrable_domain" },
                filter: "\"status\" = 'leased'");

            migrationBuilder.CreateIndex(
                name: "ix_fetch_tasks_leased_until",
                table: "fetch_tasks",
                columns: new[] { "status", "lease_until" },
                filter: "\"status\" = 'leased'");

            migrationBuilder.CreateIndex(
                name: "ix_fetch_tasks_source_id_user_id",
                table: "fetch_tasks",
                columns: new[] { "source_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fetch_tasks_status_not_before",
                table: "fetch_tasks",
                columns: new[] { "status", "not_before" });

            migrationBuilder.CreateIndex(
                name: "ix_fetch_tasks_user_id",
                table: "fetch_tasks",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_fetch_tasks_live_source",
                table: "fetch_tasks",
                column: "source_id",
                unique: true,
                filter: "\"source_id\" IS NOT NULL AND \"status\" IN ('queued', 'leased')");

            migrationBuilder.CreateIndex(
                name: "ix_source_creations_user_created",
                table: "source_creations",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_story_sources_episode_created",
                table: "story_sources",
                columns: new[] { "episode_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_story_sources_episode_id_user_id",
                table: "story_sources",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_story_sources_episode_status",
                table: "story_sources",
                columns: new[] { "episode_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_story_sources_user_id",
                table: "story_sources",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_story_sources_episode_url",
                table: "story_sources",
                columns: new[] { "episode_id", "normalized_url" },
                unique: true,
                filter: "\"kind\" = 'url'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fetch_tasks");

            migrationBuilder.DropTable(
                name: "source_creations");

            migrationBuilder.DropTable(
                name: "story_sources");
        }
    }
}
