using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_GenerationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "fact_sheet_id",
                table: "story_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "generation_run_id",
                table: "story_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_version_id",
                table: "story_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "schema_version",
                table: "story_versions",
                type: "text",
                nullable: false,
                defaultValue: "story.v1");

            migrationBuilder.CreateTable(
                name: "generation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    previous_episode_status = table.Column<string>(type: "text", nullable: true),
                    input_fact_sheet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counts_toward_limit = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    worst_case_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_detail = table.Column<string>(type: "text", nullable: true),
                    pause_reason = table.Column<string>(type: "text", nullable: true),
                    result_story_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_fact_sheet_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    models = table.Column<string>(type: "jsonb", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generation_runs", x => x.id);
                    table.UniqueConstraint("ak_generation_runs_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_generation_runs_failed", "\"status\" <> 'failed' OR \"error_code\" IS NOT NULL");
                    table.CheckConstraint("ck_generation_runs_kind", "\"kind\" IN ('bible_proposal', 'fact_sheet', 'find_sources', 'ideas', 'story')");
                    table.CheckConstraint("ck_generation_runs_money", "\"worst_case_usd\" >= 0 AND (\"cost_usd\" IS NULL OR \"cost_usd\" >= 0)");
                    table.CheckConstraint("ck_generation_runs_pause", "\"pause_reason\" IS NULL OR \"pause_reason\" IN ('spend_cap_reached', 'provider_funds_exhausted')");
                    table.CheckConstraint("ck_generation_runs_prev", "\"previous_episode_status\" IS NULL OR \"previous_episode_status\" IN ('draft', 'story_review')");
                    table.CheckConstraint("ck_generation_runs_scope", "(\"episode_id\" IS NULL) = (\"kind\" IN ('ideas', 'bible_proposal'))");
                    table.CheckConstraint("ck_generation_runs_stage", "\"stage\" IN ('checking', 'done', 'queued', 'reviewing', 'revising', 'saving', 'scoring', 'writing')");
                    table.CheckConstraint("ck_generation_runs_status", "\"status\" IN ('cancelled', 'failed', 'queued', 'running', 'succeeded')");
                    table.CheckConstraint("ck_generation_runs_times", "\"finished_at\" IS NULL OR \"started_at\" IS NULL OR \"finished_at\" >= \"started_at\"");
                    table.CheckConstraint("ck_generation_runs_trigger", "\"trigger\" IN ('autopilot', 'system', 'user')");
                    table.ForeignKey(
                        name: "fk_generation_runs_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generation_runs_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_generation_runs_series_series_id_user_id",
                        columns: x => new { x.series_id, x.user_id },
                        principalTable: "series",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generation_runs_story_versions_result_story_version_id_user",
                        columns: x => new { x.result_story_version_id, x.user_id },
                        principalTable: "story_versions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_generation_run_id",
                table: "story_versions",
                column: "generation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_generation_run_id_user_id",
                table: "story_versions",
                columns: new[] { "generation_run_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_parent_version_id_user_id",
                table: "story_versions",
                columns: new[] { "parent_version_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_story_versions_episode_version",
                table: "story_versions",
                columns: new[] { "episode_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_episode_id_user_id",
                table: "generation_runs",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_episode_kind_queued",
                table: "generation_runs",
                columns: new[] { "episode_id", "kind", "queued_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_open",
                table: "generation_runs",
                column: "status",
                filter: "\"status\" IN ('queued', 'running')")
                .Annotation("Npgsql:IndexInclude", new[] { "worst_case_usd" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_result_story_version_id_user_id",
                table: "generation_runs",
                columns: new[] { "result_story_version_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_series_id_user_id",
                table: "generation_runs",
                columns: new[] { "series_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_user_id",
                table: "generation_runs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_generation_runs_active_episode",
                table: "generation_runs",
                columns: new[] { "episode_id", "kind" },
                unique: true,
                filter: "\"episode_id\" IS NOT NULL AND \"status\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "ux_generation_runs_active_series",
                table: "generation_runs",
                columns: new[] { "series_id", "kind" },
                unique: true,
                filter: "\"episode_id\" IS NULL AND \"status\" IN ('queued', 'running')");

            migrationBuilder.AddForeignKey(
                name: "fk_story_versions_generation_runs_generation_run_id_user_id",
                table: "story_versions",
                columns: new[] { "generation_run_id", "user_id" },
                principalTable: "generation_runs",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_story_versions_story_versions_parent_version_id_user_id",
                table: "story_versions",
                columns: new[] { "parent_version_id", "user_id" },
                principalTable: "story_versions",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            // NTS-922-SEC-001 (09 W3 CR-46): the three nullable same-owner foreign keys above were created with
            // EF's plain `ON DELETE SET NULL`, which nulls both key columns; `user_id` is NOT NULL, so the first
            // delete of a parent would fail instead of clearing the reference. PostgreSQL 15+ spells the rule
            // as a column list, which EF cannot emit (see InitialCore), so each constraint is replaced with the
            // same definition plus that list. Regression: TenantForeignKeyTests.
            migrationBuilder.Sql(
                """
                ALTER TABLE generation_runs
                    DROP CONSTRAINT fk_generation_runs_story_versions_result_story_version_id_user;

                ALTER TABLE generation_runs
                    ADD CONSTRAINT fk_generation_runs_story_versions_result_story_version_id_user
                        FOREIGN KEY (result_story_version_id, user_id) REFERENCES story_versions (id, user_id)
                        ON DELETE SET NULL (result_story_version_id);

                ALTER TABLE story_versions
                    DROP CONSTRAINT fk_story_versions_generation_runs_generation_run_id_user_id;

                ALTER TABLE story_versions
                    ADD CONSTRAINT fk_story_versions_generation_runs_generation_run_id_user_id
                        FOREIGN KEY (generation_run_id, user_id) REFERENCES generation_runs (id, user_id)
                        ON DELETE SET NULL (generation_run_id);

                ALTER TABLE story_versions
                    DROP CONSTRAINT fk_story_versions_story_versions_parent_version_id_user_id;

                ALTER TABLE story_versions
                    ADD CONSTRAINT fk_story_versions_story_versions_parent_version_id_user_id
                        FOREIGN KEY (parent_version_id, user_id) REFERENCES story_versions (id, user_id)
                        ON DELETE SET NULL (parent_version_id);
                """);

            // The sweeper's scan (02.2.2 plan §7.3) orders by an expression EF cannot model, so the index lives
            // only here; dropping the table in Down drops it too.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_generation_runs_sweep
                    ON generation_runs (status, COALESCE(started_at, queued_at));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_story_versions_generation_runs_generation_run_id_user_id",
                table: "story_versions");

            migrationBuilder.DropForeignKey(
                name: "fk_story_versions_story_versions_parent_version_id_user_id",
                table: "story_versions");

            migrationBuilder.DropTable(
                name: "generation_runs");

            migrationBuilder.DropIndex(
                name: "ix_story_versions_generation_run_id",
                table: "story_versions");

            migrationBuilder.DropIndex(
                name: "ix_story_versions_generation_run_id_user_id",
                table: "story_versions");

            migrationBuilder.DropIndex(
                name: "ix_story_versions_parent_version_id_user_id",
                table: "story_versions");

            migrationBuilder.DropIndex(
                name: "ux_story_versions_episode_version",
                table: "story_versions");

            migrationBuilder.DropColumn(
                name: "fact_sheet_id",
                table: "story_versions");

            migrationBuilder.DropColumn(
                name: "generation_run_id",
                table: "story_versions");

            migrationBuilder.DropColumn(
                name: "parent_version_id",
                table: "story_versions");

            migrationBuilder.DropColumn(
                name: "schema_version",
                table: "story_versions");
        }
    }
}
