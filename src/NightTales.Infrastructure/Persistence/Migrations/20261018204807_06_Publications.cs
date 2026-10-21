using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _06_Publications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "publications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    privacy = table.Column<string>(type: "text", nullable: false),
                    publish_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    veto_ends_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    consent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    consent_text_version = table.Column<string>(type: "text", nullable: true),
                    consent_source = table.Column<string>(type: "text", nullable: true),
                    autopilot_authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    veto_notice_sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    contains_synthetic_media = table.Column<bool>(type: "boolean", nullable: false),
                    external_post_id = table.Column<string>(type: "text", nullable: true),
                    external_url = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    platform_payload = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    self_declared_made_for_kids = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_publications", x => x.id);
                    table.CheckConstraint("ck_publications_consent_source", "consent_source IN ('user','autopilot')");
                    table.CheckConstraint("ck_publications_platform", "platform IN ('youtube','tiktok')");
                    table.CheckConstraint("ck_publications_privacy", "privacy IN ('public','unlisted','private')");
                    table.CheckConstraint("ck_publications_status", "\"status\" IN ('awaiting_consent', 'cancelled', 'failed', 'processing', 'published', 'scheduled', 'uploading')");
                    table.ForeignKey(
                        name: "fk_publications_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_publications_channel_connections_channel_connection_id",
                        column: x => x.channel_connection_id,
                        principalTable: "channel_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_publications_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_publications_series_autopilot_authorizations_autopilot_auth",
                        column: x => x.autopilot_authorization_id,
                        principalTable: "series_autopilot_authorizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_publications_autopilot_authorization_id",
                table: "publications",
                column: "autopilot_authorization_id");

            migrationBuilder.CreateIndex(
                name: "ix_publications_channel_connection_id",
                table: "publications",
                column: "channel_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_publications_episode_id_user_id",
                table: "publications",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_publications_status_next_attempt_at",
                table: "publications",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status IN ('awaiting_consent','scheduled','uploading','processing')");

            migrationBuilder.CreateIndex(
                name: "ix_publications_user_id_created_at",
                table: "publications",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_publications_active_episode_platform",
                table: "publications",
                columns: new[] { "episode_id", "platform" },
                unique: true,
                filter: "status NOT IN ('failed','cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "publications");
        }
    }
}
