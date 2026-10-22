using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _06_PlatformIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    oauth_client_id = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    cleared_by = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_incidents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_platform_incidents_raised_at",
                table: "platform_incidents",
                column: "raised_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ux_platform_incidents_open",
                table: "platform_incidents",
                columns: new[] { "platform", "oauth_client_id" },
                unique: true,
                filter: "cleared_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_incidents");
        }
    }
}
