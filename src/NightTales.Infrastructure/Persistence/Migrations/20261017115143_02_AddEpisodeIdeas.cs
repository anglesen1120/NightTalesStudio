using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_AddEpisodeIdeas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "episode_ideas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    logline = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    hook = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    setting = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    structure = table.Column<string>(type: "jsonb", nullable: false),
                    novelty = table.Column<string>(type: "jsonb", nullable: true),
                    score = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    similarity = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    rejected_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_episode_ideas", x => x.id);
                    table.CheckConstraint("ck_episode_ideas_status", "\"status\" IN ('picked', 'proposed', 'rejected', 'used')");
                    table.ForeignKey(
                        name: "fk_episode_ideas_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_episode_ideas_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_episode_ideas_generation_runs_generation_run_id_user_id",
                        columns: x => new { x.generation_run_id, x.user_id },
                        principalTable: "generation_runs",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_episode_ideas_series_series_id_user_id",
                        columns: x => new { x.series_id, x.user_id },
                        principalTable: "series",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_episode_ideas_episode_id_user_id",
                table: "episode_ideas",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_episode_ideas_generation_run",
                table: "episode_ideas",
                column: "generation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_episode_ideas_generation_run_id_user_id",
                table: "episode_ideas",
                columns: new[] { "generation_run_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_episode_ideas_series_id_user_id",
                table: "episode_ideas",
                columns: new[] { "series_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_episode_ideas_series_status_created",
                table: "episode_ideas",
                columns: new[] { "series_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_episode_ideas_user_id",
                table: "episode_ideas",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "UX_episode_ideas_episode",
                table: "episode_ideas",
                column: "episode_id",
                unique: true,
                filter: "\"episode_id\" IS NOT NULL");

            // NTS-922-SEC-001 (09 W3 CR-46): the nullable same-owner foreign key above is created with EF's plain
            // `ON DELETE SET NULL`, which nulls both key columns; `user_id` is NOT NULL, so the first deleted
            // episode would fail with a not-null violation instead of clearing the reference. PostgreSQL 15+ spells
            // the rule as a column list, which EF cannot emit (see 02_AddSeriesBible), so the constraint is replaced
            // with the same definition plus that list. The model snapshot still describes the relationship as
            // SetNull. Regression: TenantForeignKeyTests, AddEpisodeIdeasMigrationTests.
            migrationBuilder.Sql(
                """
                ALTER TABLE episode_ideas
                    DROP CONSTRAINT fk_episode_ideas_episodes_episode_id_user_id;

                ALTER TABLE episode_ideas
                    ADD CONSTRAINT fk_episode_ideas_episodes_episode_id_user_id
                        FOREIGN KEY (episode_id, user_id) REFERENCES episodes (id, user_id)
                        ON DELETE SET NULL (episode_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episode_ideas");
        }
    }
}
