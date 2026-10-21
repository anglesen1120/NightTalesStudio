using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _13_DataDeletionRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_deletion_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    reapplied_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_deletion_requests", x => x.id);
                    table.CheckConstraint("ck_data_deletion_requests_reason", "\"reason\" IN ('account_delete', 'google_revoke', 'in_app_revoke', 'user_request')");
                    table.CheckConstraint("ck_data_deletion_requests_scope", "\"scope\" IN ('account', 'channel', 'episode')");
                    table.CheckConstraint("ck_data_deletion_requests_status", "\"status\" IN ('completed', 'failed', 'in_progress', 'pending')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_deletion_requests_due",
                table: "data_deletion_requests",
                column: "due_at",
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deletion_requests_reapply",
                table: "data_deletion_requests",
                columns: new[] { "completed_at", "id" },
                filter: "completed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deletion_requests_user",
                table: "data_deletion_requests",
                columns: new[] { "user_id", "requested_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_deletion_requests_open",
                table: "data_deletion_requests",
                columns: new[] { "user_id", "scope", "target_id", "reason" },
                unique: true,
                filter: "completed_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_deletion_requests");
        }
    }
}
