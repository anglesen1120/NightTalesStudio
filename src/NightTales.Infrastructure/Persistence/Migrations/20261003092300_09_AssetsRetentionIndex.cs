using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _09_AssetsRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_assets_retention_due",
                table: "assets",
                column: "retention_until",
                filter: "deleted_at IS NULL AND retention_until IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_assets_retention_due",
                table: "assets");
        }
    }
}
