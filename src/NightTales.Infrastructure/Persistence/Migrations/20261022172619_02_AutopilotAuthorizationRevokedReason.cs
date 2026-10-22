using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _02_AutopilotAuthorizationRevokedReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "revoked_reason",
                table: "series_autopilot_authorizations",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_series_autopilot_authorizations_revoked_reason",
                table: "series_autopilot_authorizations",
                sql: "\"revoked_reason\" IN ('channel_changed', 'disabled', 'owner_revoked', 'superseded', 'text_changed', 'topic_changed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_series_autopilot_authorizations_revoked_reason",
                table: "series_autopilot_authorizations");

            migrationBuilder.DropColumn(
                name: "revoked_reason",
                table: "series_autopilot_authorizations");
        }
    }
}
