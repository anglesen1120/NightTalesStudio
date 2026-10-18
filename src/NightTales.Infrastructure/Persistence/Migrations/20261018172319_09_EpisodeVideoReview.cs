using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _09_EpisodeVideoReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 09 CR-48 (09.7.1-T1): checkpoint #2 stores its own decision on the episode — `ready_at` and the
            // rejection reason. Expand-only: both columns are nullable and there is no backfill (every existing
            // row keeps NULL and means "no video decision yet"), so the previous release keeps reading and
            // writing the table unchanged. No destructive step, hence no `[ContractMigration]`; the same-owner
            // alternate key and every index of `episodes` are untouched.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ready_at",
                table: "episodes",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "video_reject_reason",
                table: "episodes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ready_at",
                table: "episodes");

            migrationBuilder.DropColumn(
                name: "video_reject_reason",
                table: "episodes");
        }
    }
}
