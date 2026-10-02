using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _09_AuthChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_challenges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    preauth_cookie_hash = table.Column<string>(type: "text", nullable: false),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_challenges", x => x.id);
                    table.CheckConstraint("ck_auth_challenges_attempts", "attempts >= 0 AND attempts <= 5");
                    table.ForeignKey(
                        name: "fk_auth_challenges_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_challenges_user_id_expires_at",
                table: "auth_challenges",
                columns: new[] { "user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_auth_challenges_token_hash",
                table: "auth_challenges",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_challenges");
        }
    }
}
