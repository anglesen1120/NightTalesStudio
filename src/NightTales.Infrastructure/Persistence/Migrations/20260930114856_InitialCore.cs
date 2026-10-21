using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NightTales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_flags",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    rules = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_flags", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    trace_parent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "style_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "text", nullable: false),
                    preview_storage_key = table.Column<string>(type: "text", nullable: true),
                    topic_hint = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_style_templates", x => x.id);
                    table.CheckConstraint("ck_style_templates_template", "position('{term}' in template) > 0");
                    table.CheckConstraint("ck_style_templates_topic_hint", "\"topic_hint\" IN ('ghost', 'spiritual', 'true_crime')");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ui_locale = table.Column<string>(type: "text", nullable: false, defaultValue: "vi"),
                    role = table.Column<string>(type: "text", nullable: false, defaultValue: "user"),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_totp_step = table.Column<long>(type: "bigint", nullable: true),
                    owner_dispute_freeze_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    email_verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "\"role\" IN ('admin', 'owner', 'user')");
                    table.CheckConstraint("ck_users_status", "\"status\" IN ('active', 'deleted', 'suspended')");
                    table.CheckConstraint("ck_users_ui_locale", "\"ui_locale\" IN ('en', 'vi')");
                });

            migrationBuilder.CreateTable(
                name: "voices",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    gender = table.Column<string>(type: "text", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    preview_storage_key = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voices", x => x.id);
                    table.CheckConstraint("ck_voices_gender", "\"gender\" IN ('female', 'male', 'neutral')");
                    table.CheckConstraint("ck_voices_language", "\"language\" IN ('en', 'vi')");
                });

            migrationBuilder.CreateTable(
                name: "role_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_claims_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    response_type = table.Column<string>(type: "text", nullable: true),
                    response = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_idempotency_keys_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_claims_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    duration_sec = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    retention_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.UniqueConstraint("ak_assets_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_assets_kind", "\"kind\" IN ('audio', 'image', 'subtitle', 'thumbnail', 'video')");
                    table.ForeignKey(
                        name: "fk_assets_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "autopilot_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    idea_id = table.Column<Guid>(type: "uuid", nullable: true),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    gate_results = table.Column<string>(type: "jsonb", nullable: true),
                    pause_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_autopilot_runs", x => x.id);
                    table.CheckConstraint("ck_autopilot_runs_outcome", "\"outcome\" IN ('awaiting_authorization', 'cancelled_by_owner', 'failed', 'gate_failed', 'paused', 'published', 'published_private_locked', 'waiting_owner')");
                    table.ForeignKey(
                        name: "fk_autopilot_runs_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "episodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    brief = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    length_tier = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_story_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_story_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_video_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_episodes", x => x.id);
                    table.UniqueConstraint("ak_episodes_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_episodes_length_tier", "\"length_tier\" IN ('long', 'short', 'standard')");
                    table.CheckConstraint("ck_episodes_status", "\"status\" IN ('cancelled', 'draft', 'failed', 'published', 'ready', 'rendering', 'scheduled', 'story_approved', 'story_generating', 'story_review', 'video_review')");
                    table.ForeignKey(
                        name: "fk_episodes_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_episodes_assets_final_video_asset_id_user_id",
                        columns: x => new { x.final_video_asset_id, x.user_id },
                        principalTable: "assets",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "moderation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    categories = table.Column<string[]>(type: "text[]", nullable: false),
                    excerpt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_events", x => x.id);
                    table.CheckConstraint("ck_moderation_events_source", "\"source\" IN ('internal', 'openai_image', 'openai_text')");
                    table.ForeignKey(
                        name: "fk_moderation_events_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_moderation_events_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<string[]>(type: "text[]", nullable: false),
                    word_count = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_version = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    audit_report = table.Column<string>(type: "jsonb", nullable: true),
                    similarity_score = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_versions", x => x.id);
                    table.UniqueConstraint("ak_story_versions_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_story_versions_source", "\"source\" IN ('edited', 'generated', 'revised')");
                    table.ForeignKey(
                        name: "fk_story_versions_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_story_versions_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "series",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    aspect = table.Column<string>(type: "text", nullable: false),
                    voice_id = table.Column<string>(type: "text", nullable: false),
                    style_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    story_preset = table.Column<string>(type: "text", nullable: true),
                    default_channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    autopilot_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    autopilot_settings = table.Column<string>(type: "jsonb", nullable: true),
                    current_autopilot_authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    autopilot_paused_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    autopilot_pause_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series", x => x.id);
                    table.UniqueConstraint("ak_series_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_series_aspect", "\"aspect\" IN ('landscape', 'portrait')");
                    table.CheckConstraint("ck_series_language", "\"language\" IN ('en', 'vi')");
                    table.CheckConstraint("ck_series_topic", "\"topic\" IN ('ghost', 'spiritual', 'true_crime')");
                    table.ForeignKey(
                        name: "fk_series_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_style_templates_style_template_id",
                        column: x => x.style_template_id,
                        principalTable: "style_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_voices_voice_id",
                        column: x => x.voice_id,
                        principalTable: "voices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "series_autopilot_authorizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    text_version = table.Column<string>(type: "text", nullable: false),
                    text_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    topic = table.Column<string>(type: "text", nullable: false),
                    channel_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cadence = table.Column<string>(type: "text", nullable: false),
                    privacy_status = table.Column<string>(type: "text", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_series_autopilot_authorizations", x => x.id);
                    table.UniqueConstraint("ak_series_autopilot_authorizations_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_series_autopilot_authorizations_privacy_status", "\"privacy_status\" IN ('private', 'public', 'unlisted')");
                    table.CheckConstraint("ck_series_autopilot_authorizations_topic", "\"topic\" IN ('ghost', 'spiritual', 'true_crime')");
                    table.ForeignKey(
                        name: "fk_series_autopilot_authorizations_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_series_autopilot_authorizations_series_series_id_user_id",
                        columns: x => new { x.series_id, x.user_id },
                        principalTable: "series",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    episode_id = table.Column<Guid>(type: "uuid", nullable: true),
                    series_id = table.Column<Guid>(type: "uuid", nullable: true),
                    generation_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    render_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cached_input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    output_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    image_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    gpu_seconds = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false, defaultValue: 0m),
                    estimated = table.Column<bool>(type: "boolean", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    price_version = table.Column<int>(type: "integer", nullable: true),
                    provider_request_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_records", x => x.id);
                    table.CheckConstraint("ck_usage_records_operation", "\"operation\" IN ('bible_proposal', 'fact_sheet', 'idea', 'image', 'moderation', 'render', 'source_find', 'story', 'story_review', 'story_revision', 'tts')");
                    table.ForeignKey(
                        name: "fk_usage_records_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_usage_records_episodes_episode_id_user_id",
                        columns: x => new { x.episode_id, x.user_id },
                        principalTable: "episodes",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_usage_records_series_series_id_user_id",
                        columns: x => new { x.series_id, x.user_id },
                        principalTable: "series",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_episode_id",
                table: "assets",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_episode_id_user_id",
                table: "assets",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_storage_key",
                table: "assets",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_user_id",
                table: "assets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_episode_id_user_id",
                table: "autopilot_runs",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_series_id_started_at",
                table: "autopilot_runs",
                columns: new[] { "series_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_series_id_user_id",
                table: "autopilot_runs",
                columns: new[] { "series_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_autopilot_runs_user_id",
                table: "autopilot_runs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_episodes_approved_story_version_id_user_id",
                table: "episodes",
                columns: new[] { "approved_story_version_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_current_story_version_id_user_id",
                table: "episodes",
                columns: new[] { "current_story_version_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_final_video_asset_id_user_id",
                table: "episodes",
                columns: new[] { "final_video_asset_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_series_id_created_at_id",
                table: "episodes",
                columns: new[] { "series_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_series_id_user_id",
                table: "episodes",
                columns: new[] { "series_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_user_id_created_at_id",
                table: "episodes",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_episodes_user_id_status_created_at_id",
                table: "episodes",
                columns: new[] { "user_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires_at",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_user_id_key",
                table: "idempotency_keys",
                columns: new[] { "user_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_moderation_events_episode_id_user_id",
                table: "moderation_events",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_events_user_id_created_at",
                table: "moderation_events",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_next_attempt_at",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "processed_at IS NULL AND failed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at",
                table: "outbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_role_claims_role_id",
                table: "role_claims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "role_name_index",
                table: "roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_series_current_autopilot_authorization_id_user_id",
                table: "series",
                columns: new[] { "current_autopilot_authorization_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_series_style_template_id",
                table: "series",
                column: "style_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_user_id",
                table: "series",
                column: "user_id",
                filter: "archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_series_user_id_created_at_id",
                table: "series",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_series_voice_id",
                table: "series",
                column: "voice_id");

            migrationBuilder.CreateIndex(
                name: "ix_series_autopilot_authorizations_series_id_user_id",
                table: "series_autopilot_authorizations",
                columns: new[] { "series_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_series_autopilot_authorizations_series_id_version",
                table: "series_autopilot_authorizations",
                columns: new[] { "series_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_series_autopilot_authorizations_user_id",
                table: "series_autopilot_authorizations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_episode_id",
                table: "story_versions",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_episode_id_user_id",
                table: "story_versions",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_story_versions_user_id",
                table: "story_versions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_style_templates_name",
                table: "style_templates",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_created_at_operation",
                table: "usage_records",
                columns: new[] { "created_at", "operation" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_episode_id_user_id",
                table: "usage_records",
                columns: new[] { "episode_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_generation_run_id",
                table: "usage_records",
                column: "generation_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_reservation_id",
                table: "usage_records",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_series_id_user_id",
                table: "usage_records",
                columns: new[] { "series_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_records_user_id_created_at",
                table: "usage_records",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_user_claims_user_id",
                table: "user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "user_name_index",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_email",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_assets_episodes_episode_id_user_id",
                table: "assets",
                columns: new[] { "episode_id", "user_id" },
                principalTable: "episodes",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_autopilot_runs_episodes_episode_id_user_id",
                table: "autopilot_runs",
                columns: new[] { "episode_id", "user_id" },
                principalTable: "episodes",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_autopilot_runs_series_series_id_user_id",
                table: "autopilot_runs",
                columns: new[] { "series_id", "user_id" },
                principalTable: "series",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_episodes_series_series_id_user_id",
                table: "episodes",
                columns: new[] { "series_id", "user_id" },
                principalTable: "series",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_episodes_story_versions_approved_story_version_id_user_id",
                table: "episodes",
                columns: new[] { "approved_story_version_id", "user_id" },
                principalTable: "story_versions",
                principalColumns: new[] { "id", "user_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_episodes_story_versions_current_story_version_id_user_id",
                table: "episodes",
                columns: new[] { "current_story_version_id", "user_id" },
                principalTable: "story_versions",
                principalColumns: new[] { "id", "user_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_series_series_autopilot_authorizations_current_autopilot_au",
                table: "series",
                columns: new[] { "current_autopilot_authorization_id", "user_id" },
                principalTable: "series_autopilot_authorizations",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.SetNull);

            // NTS-922-SEC-001 (09 W3 CR-46, owner decision 2026-09-30, option (a)).
            // The four nullable same-owner foreign keys above were created with EF's plain
            // `ON DELETE SET NULL`, which nulls *both* key columns; `user_id` is NOT NULL, so the first
            // delete of a parent would fail with a not-null violation instead of clearing the reference.
            // PostgreSQL 15+ spells the rule as a column list — `ON DELETE SET NULL (parent_column)` —
            // which EF Core cannot emit, so each constraint is replaced with the same definition plus
            // that list. The model snapshot still describes the relationship as SetNull; only the
            // delete action's column list lives here. Regression: TenantForeignKeyTests asserts every
            // one of these definitions.
            migrationBuilder.Sql(
                """
                ALTER TABLE episodes
                    DROP CONSTRAINT fk_episodes_assets_final_video_asset_id_user_id;

                ALTER TABLE episodes
                    ADD CONSTRAINT fk_episodes_assets_final_video_asset_id_user_id
                        FOREIGN KEY (final_video_asset_id, user_id) REFERENCES assets (id, user_id)
                        ON DELETE SET NULL (final_video_asset_id);

                ALTER TABLE autopilot_runs
                    DROP CONSTRAINT fk_autopilot_runs_episodes_episode_id_user_id;

                ALTER TABLE autopilot_runs
                    ADD CONSTRAINT fk_autopilot_runs_episodes_episode_id_user_id
                        FOREIGN KEY (episode_id, user_id) REFERENCES episodes (id, user_id)
                        ON DELETE SET NULL (episode_id);

                ALTER TABLE usage_records
                    DROP CONSTRAINT fk_usage_records_episodes_episode_id_user_id;

                ALTER TABLE usage_records
                    ADD CONSTRAINT fk_usage_records_episodes_episode_id_user_id
                        FOREIGN KEY (episode_id, user_id) REFERENCES episodes (id, user_id)
                        ON DELETE SET NULL (episode_id);

                ALTER TABLE series
                    DROP CONSTRAINT fk_series_series_autopilot_authorizations_current_autopilot_au;

                ALTER TABLE series
                    ADD CONSTRAINT fk_series_series_autopilot_authorizations_current_autopilot_au
                        FOREIGN KEY (current_autopilot_authorization_id, user_id)
                        REFERENCES series_autopilot_authorizations (id, user_id)
                        ON DELETE SET NULL (current_autopilot_authorization_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_assets_asp_net_users_user_id",
                table: "assets");

            migrationBuilder.DropForeignKey(
                name: "fk_episodes_asp_net_users_user_id",
                table: "episodes");

            migrationBuilder.DropForeignKey(
                name: "fk_series_asp_net_users_user_id",
                table: "series");

            migrationBuilder.DropForeignKey(
                name: "fk_series_autopilot_authorizations_asp_net_users_user_id",
                table: "series_autopilot_authorizations");

            migrationBuilder.DropForeignKey(
                name: "fk_story_versions_asp_net_users_user_id",
                table: "story_versions");

            migrationBuilder.DropForeignKey(
                name: "fk_assets_episodes_episode_id_user_id",
                table: "assets");

            migrationBuilder.DropForeignKey(
                name: "fk_story_versions_episodes_episode_id_user_id",
                table: "story_versions");

            migrationBuilder.DropForeignKey(
                name: "fk_series_autopilot_authorizations_series_series_id_user_id",
                table: "series_autopilot_authorizations");

            migrationBuilder.DropTable(
                name: "autopilot_runs");

            migrationBuilder.DropTable(
                name: "feature_flags");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "moderation_events");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "role_claims");

            migrationBuilder.DropTable(
                name: "usage_records");

            migrationBuilder.DropTable(
                name: "user_claims");

            migrationBuilder.DropTable(
                name: "user_logins");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "episodes");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "story_versions");

            migrationBuilder.DropTable(
                name: "series");

            migrationBuilder.DropTable(
                name: "series_autopilot_authorizations");

            migrationBuilder.DropTable(
                name: "style_templates");

            migrationBuilder.DropTable(
                name: "voices");
        }
    }
}
