using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_PriceVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "price_versions",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false),
                    active_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    rates = table.Column<string>(type: "jsonb", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    config_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_versions", x => x.version);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_generation_run_id_user_id",
                table: "usage_records",
                columns: new[] { "generation_run_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_price_version",
                table: "usage_records",
                column: "price_version");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_records_cost",
                table: "usage_records",
                sql: "\"cost_usd\" IS NULL OR \"cost_usd\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_records_tokens",
                table: "usage_records",
                sql: "\"input_tokens\" >= 0 AND \"cached_input_tokens\" >= 0 AND \"output_tokens\" >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_price_versions_active_from",
                table: "price_versions",
                column: "active_from",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_price_versions_config_hash",
                table: "price_versions",
                column: "config_hash");

            migrationBuilder.AddForeignKey(
                name: "fk_usage_records_generation_runs_generation_run_id_user_id",
                table: "usage_records",
                columns: new[] { "generation_run_id", "user_id" },
                principalTable: "generation_runs",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_usage_records_price_versions_price_version",
                table: "usage_records",
                column: "price_version",
                principalTable: "price_versions",
                principalColumn: "version",
                onDelete: ReferentialAction.Restrict);

            // NTS-922-SEC-001 (09 W3 CR-46): the same-owner run FK was created with EF's plain `ON DELETE SET NULL`,
            // which nulls both key columns; `user_id` is NOT NULL, so the first delete of a run would fail instead
            // of clearing the reference. PostgreSQL 15+ spells the rule as a column list, which EF cannot emit, so
            // the constraint is replaced with the same definition plus that list (as 02_GenerationRuns does).
            // Regression: TenantForeignKeyTests.
            migrationBuilder.Sql(
                """
                ALTER TABLE usage_records
                    DROP CONSTRAINT fk_usage_records_generation_runs_generation_run_id_user_id;

                ALTER TABLE usage_records
                    ADD CONSTRAINT fk_usage_records_generation_runs_generation_run_id_user_id
                        FOREIGN KEY (generation_run_id, user_id) REFERENCES generation_runs (id, user_id)
                        ON DELETE SET NULL (generation_run_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_usage_records_generation_runs_generation_run_id_user_id",
                table: "usage_records");

            migrationBuilder.DropForeignKey(
                name: "fk_usage_records_price_versions_price_version",
                table: "usage_records");

            migrationBuilder.DropTable(
                name: "price_versions");

            migrationBuilder.DropIndex(
                name: "ix_usage_records_generation_run_id_user_id",
                table: "usage_records");

            migrationBuilder.DropIndex(
                name: "ix_usage_records_price_version",
                table: "usage_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_records_cost",
                table: "usage_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_records_tokens",
                table: "usage_records");
        }
    }
}
