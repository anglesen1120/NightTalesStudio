using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_AddFactSheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fact_sheets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    facts = table.Column<string>(type: "jsonb", nullable: false),
                    source_aliases = table.Column<string>(type: "jsonb", nullable: false),
                    input_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    max_fact_id = table.Column<int>(type: "integer", nullable: false),
                    content_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    generation_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fact_sheets", x => x.id);
                    table.UniqueConstraint("ak_fact_sheets_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_fact_sheets_sha256", "\"content_sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_fact_sheets_status", "\"status\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "fk_fact_sheets_asp_net_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fact_sheets_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_fact_sheets_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_fact_sheets_generation_runs_generation_run_id_user_id",
                        columns: x => new { x.generation_run_id, x.user_id },
                        principalTable: "generation_runs",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_fact_sheet_id_user_id",
                table: "story_versions",
                columns: new[] { "fact_sheet_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_episode_created",
                table: "generation_runs",
                columns: new[] { "episode_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_input_fact_sheet_id_user_id",
                table: "generation_runs",
                columns: new[] { "input_fact_sheet_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_runs_result_fact_sheet_id_user_id",
                table: "generation_runs",
                columns: new[] { "result_fact_sheet_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_sheets_created_by",
                table: "fact_sheets",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_fact_sheets_episode_id_user_id",
                table: "fact_sheets",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_sheets_generation_run_id",
                table: "fact_sheets",
                column: "generation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_fact_sheets_generation_run_id_user_id",
                table: "fact_sheets",
                columns: new[] { "generation_run_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fact_sheets_user_id",
                table: "fact_sheets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_fact_sheets_episode_approved",
                table: "fact_sheets",
                column: "episode_id",
                unique: true,
                filter: "\"status\" = 1");

            migrationBuilder.CreateIndex(
                name: "ux_fact_sheets_episode_version",
                table: "fact_sheets",
                columns: new[] { "episode_id", "version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id",
                table: "generation_runs",
                columns: new[] { "input_fact_sheet_id", "user_id" },
                principalTable: "fact_sheets",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id",
                table: "generation_runs",
                columns: new[] { "result_fact_sheet_id", "user_id" },
                principalTable: "fact_sheets",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_story_versions_fact_sheets_fact_sheet_id_user_id",
                table: "story_versions",
                columns: new[] { "fact_sheet_id", "user_id" },
                principalTable: "fact_sheets",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            // NTS-922-SEC-001 (09 W3 CR-46): the four nullable same-owner foreign keys above were created
            // with EF's plain `ON DELETE SET NULL`, which nulls *both* key columns; `user_id` is NOT NULL,
            // so the first delete of a parent would fail with a not-null violation instead of clearing the
            // reference. PostgreSQL 15+ spells the rule as a column list, which EF cannot emit (see
            // InitialCore), so each constraint is replaced with the same definition plus that list. The
            // model snapshot still describes all four relationships as SetNull; only the delete action's
            // column list lives here. Regression: TenantForeignKeyTests.
            migrationBuilder.Sql(
                """
                ALTER TABLE fact_sheets
                    DROP CONSTRAINT fk_fact_sheets_generation_runs_generation_run_id_user_id;

                ALTER TABLE fact_sheets
                    ADD CONSTRAINT fk_fact_sheets_generation_runs_generation_run_id_user_id
                        FOREIGN KEY (generation_run_id, user_id) REFERENCES generation_runs (id, user_id)
                        ON DELETE SET NULL (generation_run_id);

                ALTER TABLE generation_runs
                    DROP CONSTRAINT fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id;

                ALTER TABLE generation_runs
                    ADD CONSTRAINT fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id
                        FOREIGN KEY (input_fact_sheet_id, user_id) REFERENCES fact_sheets (id, user_id)
                        ON DELETE SET NULL (input_fact_sheet_id);

                ALTER TABLE generation_runs
                    DROP CONSTRAINT fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id;

                ALTER TABLE generation_runs
                    ADD CONSTRAINT fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id
                        FOREIGN KEY (result_fact_sheet_id, user_id) REFERENCES fact_sheets (id, user_id)
                        ON DELETE SET NULL (result_fact_sheet_id);

                ALTER TABLE story_versions
                    DROP CONSTRAINT fk_story_versions_fact_sheets_fact_sheet_id_user_id;

                ALTER TABLE story_versions
                    ADD CONSTRAINT fk_story_versions_fact_sheets_fact_sheet_id_user_id
                        FOREIGN KEY (fact_sheet_id, user_id) REFERENCES fact_sheets (id, user_id)
                        ON DELETE SET NULL (fact_sheet_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id",
                table: "generation_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id",
                table: "generation_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_story_versions_fact_sheets_fact_sheet_id_user_id",
                table: "story_versions");

            migrationBuilder.DropTable(
                name: "fact_sheets");

            migrationBuilder.DropIndex(
                name: "ix_story_versions_fact_sheet_id_user_id",
                table: "story_versions");

            migrationBuilder.DropIndex(
                name: "ix_generation_runs_episode_created",
                table: "generation_runs");

            migrationBuilder.DropIndex(
                name: "ix_generation_runs_input_fact_sheet_id_user_id",
                table: "generation_runs");

            migrationBuilder.DropIndex(
                name: "ix_generation_runs_result_fact_sheet_id_user_id",
                table: "generation_runs");
        }
    }
}
