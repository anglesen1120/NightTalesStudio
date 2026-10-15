using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _06_PlatformQuotaUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_quota_usage",
                columns: table => new
                {
                    platform = table.Column<string>(type: "text", nullable: false),
                    quota_day = table.Column<DateOnly>(type: "date", nullable: false),
                    units_used = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    units_limit = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_quota_usage", x => new { x.platform, x.quota_day });
                    table.CheckConstraint("ck_platform_quota_usage_platform", "platform IN ('youtube')");
                    table.CheckConstraint("ck_platform_quota_usage_units_limit", "units_limit > 0");
                    table.CheckConstraint("ck_platform_quota_usage_units_used", "units_used >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_quota_usage");
        }
    }
}
