using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _06_ChannelConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    access_token_enc = table.Column<string>(type: "text", nullable: true),
                    refresh_token_enc = table.Column<string>(type: "text", nullable: true),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    scope = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    oauth_client_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_connections", x => x.id);
                    table.CheckConstraint("ck_channel_connections_platform", "platform IN ('youtube','tiktok')");
                    table.CheckConstraint("ck_channel_connections_status", "\"status\" IN ('active', 'error', 'expired', 'revoked')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_connections_user_id",
                table: "channel_connections",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_channel_connections_account",
                table: "channel_connections",
                columns: new[] { "platform", "external_account_id" },
                unique: true,
                filter: "status <> 'revoked'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_connections");
        }
    }
}
