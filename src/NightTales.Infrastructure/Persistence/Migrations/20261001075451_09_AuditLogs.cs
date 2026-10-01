﻿using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _09_AuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    visibility = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_logs_actor_type", "\"actor_type\" IN ('admin', 'system', 'user')");
                    table.CheckConstraint("ck_audit_logs_category", "\"category\" IN ('account_security', 'billing', 'episodes', 'publishing', 'support')");
                    table.CheckConstraint("ck_audit_logs_user_visible_has_subject", "\"visibility\" <> 'user' OR subject_user_id IS NOT NULL");
                    table.CheckConstraint("ck_audit_logs_visibility", "\"visibility\" IN ('internal', 'user')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_id",
                table: "audit_logs",
                columns: new[] { "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" },
                filter: "entity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_subject_created",
                table: "audit_logs",
                columns: new[] { "subject_user_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");
        }
    }
}
