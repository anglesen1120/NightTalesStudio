using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _06_OAuthStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    state_hash = table.Column<string>(type: "text", nullable: false),
                    code_verifier_protected = table.Column<string>(type: "text", nullable: false),
                    oauth_client_id = table.Column<string>(type: "text", nullable: false),
                    refresh_family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    browser_nonce_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_states", x => x.id);
                    table.CheckConstraint("ck_oauth_states_platform", "platform IN ('youtube','tiktok')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_states_user_created",
                table: "oauth_states",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_oauth_states_state_hash",
                table: "oauth_states",
                column: "state_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_states");
        }
    }
}
