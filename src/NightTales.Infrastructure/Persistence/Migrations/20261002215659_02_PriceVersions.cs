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
                name: "ix_price_versions_active_from",
                table: "price_versions",
                column: "active_from",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_price_versions_config_hash",
                table: "price_versions",
                column: "config_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_versions");
        }
    }
}
