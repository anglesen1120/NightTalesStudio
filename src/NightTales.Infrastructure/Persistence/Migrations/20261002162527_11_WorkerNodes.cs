using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _11_WorkerNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "worker_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    base_url = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    api_key_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    api_key_hint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    gpu_model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tls_cert_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_nodes", x => x.id);
                    table.CheckConstraint("ck_worker_nodes_capacity", "\"capacity\" BETWEEN 1 AND 8");
                    table.CheckConstraint("ck_worker_nodes_state", "\"state\" IN ('active', 'down', 'draining')");
                    table.CheckConstraint("ck_worker_nodes_tls_sha256", "CASE WHEN \"tls_cert_sha256\" IS NULL THEN \"base_url\" LIKE 'http://%' ELSE \"tls_cert_sha256\" ~ '^[0-9A-F]{64}$' END");
                });

            migrationBuilder.CreateIndex(
                name: "ux_worker_nodes_name",
                table: "worker_nodes",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_nodes");
        }
    }
}
