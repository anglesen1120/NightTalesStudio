using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// The 09.2.2 plan §3 contract of the core set, read from the live catalog instead of from the model:
/// every column with its type, length/precision/nullability, every enum <c>CHECK</c> with its exact
/// snake_case value list, every foreign key with its <c>ON DELETE</c> action and every index with its
/// key order, <c>DESC</c> markers, uniqueness and partial filter (09.2.2-T1, 09 W3 CR-38/-39/-40/-41).
/// </summary>
/// <remarks>
/// The assertions are containment-based on purpose: a later owning task adds its own columns to these
/// tables (expand-only, e.g. 02.2.2-T1 on <c>story_versions</c>), and that must not turn this test red.
/// What this test does pin is that nothing on the §3 list is missing or mapped differently.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CoreSchemaColumnContractTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>One expected column: <c>table|column|data_type|max_length|precision|scale|nullable</c>.</summary>
    private static readonly string[] ExpectedColumns =
    [
        // users — the extra columns of plan §3 (09 W3 CR-39) plus the unique e-mail column (F12).
        "users|id|uuid||||NO",
        "users|display_name|character varying|80|||NO",
        "users|ui_locale|text||||NO",
        "users|role|text||||NO",
        "users|status|text||||NO",
        "users|last_totp_step|bigint||64|0|YES",
        "users|owner_dispute_freeze_until|timestamp with time zone||||YES",
        "users|last_login_at|timestamp with time zone||||YES",
        "users|email_verified_at|timestamp with time zone||||YES",
        "users|created_at|timestamp with time zone||||NO",
        "users|updated_at|timestamp with time zone||||NO",
        "users|email|character varying|256|||YES",
        "users|normalized_email|character varying|256|||YES",
        "users|security_stamp|text||||YES",
        "users|two_factor_enabled|boolean||||NO",

        // series
        "series|id|uuid||||NO",
        "series|user_id|uuid||||NO",
        "series|title|character varying|120|||NO",
        "series|topic|text||||NO",
        "series|language|text||||NO",
        "series|aspect|text||||NO",
        "series|voice_id|text||||NO",
        "series|style_template_id|uuid||||NO",
        "series|story_preset|text||||YES",
        "series|default_channel_id|uuid||||YES",
        "series|archived_at|timestamp with time zone||||YES",
        "series|autopilot_enabled|boolean||||NO",
        "series|autopilot_settings|jsonb||||YES",
        "series|current_autopilot_authorization_id|uuid||||YES",
        "series|autopilot_paused_at|timestamp with time zone||||YES",
        "series|autopilot_pause_reason|text||||YES",
        "series|created_at|timestamp with time zone||||NO",
        "series|updated_at|timestamp with time zone||||NO",

        // episodes
        "episodes|id|uuid||||NO",
        "episodes|user_id|uuid||||NO",
        "episodes|series_id|uuid||||NO",
        "episodes|title|character varying|120|||YES",
        "episodes|brief|character varying|1000|||NO",
        "episodes|length_tier|text||||NO",
        "episodes|status|text||||NO",
        "episodes|current_story_version_id|uuid||||YES",
        "episodes|approved_story_version_id|uuid||||YES",
        "episodes|final_video_asset_id|uuid||||YES",
        "episodes|created_at|timestamp with time zone||||NO",
        "episodes|updated_at|timestamp with time zone||||NO",

        // story_versions (immutable: created_at only, 09 W3 CR-38)
        "story_versions|id|uuid||||NO",
        "story_versions|user_id|uuid||||NO",
        "story_versions|episode_id|uuid||||NO",
        "story_versions|version|integer||32|0|NO",
        "story_versions|source|text||||NO",
        "story_versions|title|text||||NO",
        "story_versions|body|text||||NO",
        "story_versions|keywords|ARRAY||||NO",
        "story_versions|word_count|integer||32|0|NO",
        "story_versions|model|text||||YES",
        "story_versions|prompt_version|text||||YES",
        "story_versions|content|jsonb||||NO",
        "story_versions|audit_report|jsonb||||YES",
        "story_versions|similarity_score|numeric||5|4|YES",
        "story_versions|created_at|timestamp with time zone||||NO",

        // series_autopilot_authorizations
        "series_autopilot_authorizations|id|uuid||||NO",
        "series_autopilot_authorizations|user_id|uuid||||NO",
        "series_autopilot_authorizations|series_id|uuid||||NO",
        "series_autopilot_authorizations|version|integer||32|0|NO",
        "series_autopilot_authorizations|text_version|text||||NO",
        "series_autopilot_authorizations|text_hash|character|64|||NO",
        "series_autopilot_authorizations|topic|text||||NO",
        "series_autopilot_authorizations|channel_connection_id|uuid||||YES",
        "series_autopilot_authorizations|cadence|text||||NO",
        "series_autopilot_authorizations|privacy_status|text||||NO",
        "series_autopilot_authorizations|accepted_at|timestamp with time zone||||NO",
        "series_autopilot_authorizations|revoked_at|timestamp with time zone||||YES",
        "series_autopilot_authorizations|created_at|timestamp with time zone||||NO",
        "series_autopilot_authorizations|updated_at|timestamp with time zone||||NO",

        // autopilot_runs
        "autopilot_runs|id|uuid||||NO",
        "autopilot_runs|user_id|uuid||||NO",
        "autopilot_runs|series_id|uuid||||NO",
        "autopilot_runs|started_at|timestamp with time zone||||NO",
        "autopilot_runs|idea_id|uuid||||YES",
        "autopilot_runs|episode_id|uuid||||YES",
        "autopilot_runs|outcome|text||||YES",
        "autopilot_runs|gate_results|jsonb||||YES",
        "autopilot_runs|pause_reason|text||||YES",
        "autopilot_runs|created_at|timestamp with time zone||||NO",
        "autopilot_runs|updated_at|timestamp with time zone||||NO",

        // moderation_events
        "moderation_events|id|uuid||||NO",
        "moderation_events|user_id|uuid||||NO",
        "moderation_events|episode_id|uuid||||YES",
        "moderation_events|source|text||||NO",
        "moderation_events|stage|text||||NO",
        "moderation_events|categories|ARRAY||||NO",
        "moderation_events|excerpt|character varying|300|||YES",
        "moderation_events|created_at|timestamp with time zone||||NO",

        // usage_records (append-only: created_at only)
        "usage_records|id|uuid||||NO",
        "usage_records|user_id|uuid||||NO",
        "usage_records|episode_id|uuid||||YES",
        "usage_records|series_id|uuid||||YES",
        "usage_records|generation_run_id|uuid||||YES",
        "usage_records|render_job_id|uuid||||YES",
        "usage_records|reservation_id|uuid||||YES",
        "usage_records|provider|text||||NO",
        "usage_records|operation|text||||NO",
        "usage_records|model|text||||YES",
        "usage_records|input_tokens|integer||32|0|NO",
        "usage_records|cached_input_tokens|integer||32|0|NO",
        "usage_records|output_tokens|integer||32|0|NO",
        "usage_records|image_count|integer||32|0|NO",
        "usage_records|gpu_seconds|numeric||10|2|NO",
        "usage_records|estimated|boolean||||NO",
        "usage_records|cost_usd|numeric||12|6|NO",
        "usage_records|price_version|integer||32|0|YES",
        "usage_records|provider_request_id|text||||YES",
        "usage_records|created_at|timestamp with time zone||||NO",

        // assets (column types owned here, index split by 09 W3 CR-40)
        "assets|id|uuid||||NO",
        "assets|user_id|uuid||||NO",
        "assets|episode_id|uuid||||NO",
        "assets|kind|text||||NO",
        "assets|storage_key|character varying|512|||NO",
        "assets|content_type|text||||NO",
        "assets|size_bytes|bigint||64|0|NO",
        "assets|duration_sec|numeric||9|2|YES",
        "assets|sha256|character|64|||NO",
        "assets|retention_until|timestamp with time zone||||YES",
        "assets|deleted_at|timestamp with time zone||||YES",
        "assets|created_at|timestamp with time zone||||NO",
        "assets|updated_at|timestamp with time zone||||NO",

        // voices
        "voices|id|text||||NO",
        "voices|display_name|text||||NO",
        "voices|language|text||||NO",
        "voices|gender|text||||NO",
        "voices|tags|ARRAY||||NO",
        "voices|preview_storage_key|text||||YES",
        "voices|active|boolean||||NO",
        "voices|created_at|timestamp with time zone||||NO",

        // style_templates
        "style_templates|id|uuid||||NO",
        "style_templates|name|text||||NO",
        "style_templates|template|text||||NO",
        "style_templates|preview_storage_key|text||||YES",
        "style_templates|topic_hint|text||||YES",
        "style_templates|active|boolean||||NO",
        "style_templates|created_at|timestamp with time zone||||NO",

        // feature_flags
        "feature_flags|key|text||||NO",
        "feature_flags|enabled|boolean||||NO",
        "feature_flags|rules|jsonb||||NO",
        "feature_flags|created_at|timestamp with time zone||||NO",
        "feature_flags|updated_at|timestamp with time zone||||NO",

        // outbox_messages (D11)
        "outbox_messages|id|uuid||||NO",
        "outbox_messages|type|text||||NO",
        "outbox_messages|payload|jsonb||||NO",
        "outbox_messages|user_id|uuid||||YES",
        "outbox_messages|occurred_at|timestamp with time zone||||NO",
        "outbox_messages|next_attempt_at|timestamp with time zone||||NO",
        "outbox_messages|attempts|integer||32|0|NO",
        "outbox_messages|processed_at|timestamp with time zone||||YES",
        "outbox_messages|failed_at|timestamp with time zone||||YES",
        "outbox_messages|error|character varying|2000|||YES",
        "outbox_messages|trace_parent|text||||YES",
        "outbox_messages|created_at|timestamp with time zone||||NO",

        // idempotency_keys (D6, 09.1.3 plan §3)
        "idempotency_keys|id|uuid||||NO",
        "idempotency_keys|user_id|uuid||||NO",
        "idempotency_keys|key|character varying|128|||NO",
        "idempotency_keys|request_hash|character|64|||NO",
        "idempotency_keys|response_type|text||||YES",
        "idempotency_keys|response|jsonb||||YES",
        "idempotency_keys|created_at|timestamp with time zone||||NO",
        "idempotency_keys|expires_at|timestamp with time zone||||NO",

        // The Identity tables of D8 (09.2.1-T1's model; the names and keys are the contract here).
        "roles|id|uuid||||NO",
        "roles|name|character varying|256|||YES",
        "roles|normalized_name|character varying|256|||YES",
        "user_roles|user_id|uuid||||NO",
        "user_roles|role_id|uuid||||NO",
        "user_claims|id|integer||32|0|NO",
        "user_claims|user_id|uuid||||NO",
        "user_logins|login_provider|text||||NO",
        "user_logins|user_id|uuid||||NO",
        "user_tokens|user_id|uuid||||NO",
        "role_claims|id|integer||32|0|NO",
        "role_claims|role_id|uuid||||NO",
    ];

    /// <summary>The 20 tables of acceptance check 1 (11 core + 7 Identity + outbox + idempotency).</summary>
    private static readonly string[] ExpectedTables =
    [
        "assets", "autopilot_runs", "episodes", "feature_flags", "idempotency_keys", "moderation_events",
        "outbox_messages", "role_claims", "roles", "series", "series_autopilot_authorizations", "story_versions",
        "style_templates", "usage_records", "user_claims", "user_logins", "user_roles", "user_tokens", "users",
        "voices",
    ];

    /// <summary>Every enum column with the snake_case value list its CHECK must allow (plan §3, D16).</summary>
    private static readonly string[] ExpectedEnumChecks =
    [
        "ck_assets_kind|audio,image,subtitle,thumbnail,video",
        "ck_autopilot_runs_outcome|awaiting_authorization,cancelled_by_owner,failed,gate_failed,paused,published,published_private_locked,waiting_owner",
        "ck_episodes_length_tier|long,short,standard",
        "ck_episodes_status|cancelled,draft,failed,published,ready,rendering,scheduled,story_approved,story_generating,story_review,video_review",
        "ck_moderation_events_source|internal,openai_image,openai_text",
        "ck_series_aspect|landscape,portrait",
        "ck_series_autopilot_authorizations_privacy_status|private,public,unlisted",
        "ck_series_autopilot_authorizations_topic|ghost,spiritual,true_crime",
        "ck_series_language|en,vi",
        "ck_series_topic|ghost,spiritual,true_crime",
        "ck_story_versions_source|edited,generated,revised",
        "ck_style_templates_topic_hint|ghost,spiritual,true_crime",
        "ck_usage_records_operation|bible_proposal,fact_sheet,idea,image,moderation,render,source_find,story,story_review,story_revision,tts",
        "ck_users_role|admin,owner,user",
        "ck_users_status|active,deleted,suspended",
        "ck_users_ui_locale|en,vi",
        "ck_voices_gender|female,male,neutral",
        "ck_voices_language|en,vi",
    ];

    /// <summary>Every non-enum CHECK of plan §3.</summary>
    private static readonly string[] ExpectedOtherChecks =
    [
        "ck_style_templates_template",
    ];

    /// <summary>
    /// Every foreign key: <c>table.column|action</c> with PostgreSQL's <c>confdeltype</c> letter
    /// (<c>a</c> NO ACTION, <c>r</c> RESTRICT, <c>c</c> CASCADE, <c>n</c> SET NULL).
    /// </summary>
    private static readonly string[] ExpectedForeignKeys =
    [
        "assets.episode_id|c",
        "assets.user_id|r",
        "autopilot_runs.episode_id|n",
        "autopilot_runs.series_id|r",
        "autopilot_runs.user_id|r",
        "episodes.approved_story_version_id|a",
        "episodes.current_story_version_id|a",
        "episodes.final_video_asset_id|n",
        "episodes.series_id|r",
        "episodes.user_id|r",
        "idempotency_keys.user_id|r",
        "moderation_events.episode_id|c",
        "moderation_events.user_id|r",
        "series.current_autopilot_authorization_id|n",
        "series.style_template_id|r",
        "series.user_id|r",
        "series.voice_id|r",
        "series_autopilot_authorizations.series_id|r",
        "series_autopilot_authorizations.user_id|r",
        "story_versions.episode_id|c",
        "story_versions.user_id|r",
        "usage_records.episode_id|n",
        "usage_records.series_id|r",
        "usage_records.user_id|r",
    ];

    /// <summary>
    /// Every index of plan §3: <c>table|keys|unique|predicate</c> — keys exactly as
    /// <c>pg_get_indexdef</c> renders them, so a missing <c>DESC</c> fails here.
    /// </summary>
    private static readonly string[] ExpectedIndexes =
    [
        "assets|episode_id|false|",
        "assets|storage_key|true|",
        "autopilot_runs|series_id, started_at DESC|false|",
        "episodes|series_id, created_at DESC, id DESC|false|",
        "episodes|user_id, created_at DESC, id DESC|false|",
        "episodes|user_id, status, created_at DESC, id DESC|false|",
        "idempotency_keys|expires_at|false|",
        "idempotency_keys|user_id, key|true|",
        "moderation_events|user_id, created_at DESC|false|",
        "outbox_messages|next_attempt_at|false|processed_at IS NULL AND failed_at IS NULL",
        "outbox_messages|processed_at|false|",
        "series|user_id, created_at DESC, id DESC|false|",
        "series|user_id|false|archived_at IS NULL",
        "series_autopilot_authorizations|series_id, version|true|",
        "story_versions|episode_id|false|",
        "style_templates|name|true|",
        "usage_records|created_at, operation|false|",
        "usage_records|generation_run_id|false|",
        "usage_records|reservation_id|false|",
        "usage_records|user_id, created_at DESC|false|",
        "users|normalized_email|true|",
    ];

    [Fact]
    public void Tables_CoreSetIsPresent()
    {
        var tables = SqlProbe.QueryStrings(
            Database,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename");

        // The class database is a copy of the run's template, which `PostgresFixture` builds by applying
        // the compiled migrations (09.2.2-T2 `InitialCore`), so `__EFMigrationsHistory` is present here
        // and every later migration adds only tables to this list.
        var missing = ExpectedTables.Where(table => !tables.Contains(table)).ToList();

        missing.ShouldBeEmpty($"plan §3 tables missing from the initial schema: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Columns_MatchPlanSection3()
    {
        var actual = SqlProbe.QueryStrings(
            Database,
            """
            SELECT table_name || '|' || column_name || '|' || data_type || '|'
                   || coalesce(character_maximum_length::text, '') || '|'
                   || coalesce(numeric_precision::text, '') || '|'
                   || coalesce(numeric_scale::text, '') || '|' || is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public'
            """).ToHashSet(StringComparer.Ordinal);

        var mismatches = ExpectedColumns.Where(expected => !actual.Contains(expected)).ToList();

        mismatches.ShouldBeEmpty(
            "these plan §3 columns are absent or mapped differently "
            + "(format table|column|data_type|max_length|precision|scale|nullable): "
            + string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void EnumChecks_ListEveryWireValue()
    {
        var definitions = SqlProbe.QueryStrings(
                Database,
                """
                SELECT conname || '=' || pg_get_constraintdef(oid)
                FROM pg_constraint
                WHERE contype = 'c' AND connamespace = 'public'::regnamespace
                """)
            .ToDictionary(
                entry => entry[..entry.IndexOf('=', StringComparison.Ordinal)],
                entry => entry[(entry.IndexOf('=', StringComparison.Ordinal) + 1)..],
                StringComparer.Ordinal);

        var problems = new List<string>();

        foreach (var expected in ExpectedEnumChecks)
        {
            var parts = expected.Split('|');
            var name = parts[0];

            if (!definitions.TryGetValue(name, out var definition))
            {
                problems.Add($"{name}: CHECK constraint is missing (plan §3)");
                continue;
            }

            var allowed = System.Text.RegularExpressions.Regex
                .Matches(definition, "'([^']*)'")
                .Select(match => match.Groups[1].Value)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            var expectedValues = parts[1].Split(',').OrderBy(value => value, StringComparer.Ordinal).ToArray();

            if (!allowed.SequenceEqual(expectedValues, StringComparer.Ordinal))
            {
                problems.Add($"{name}: allowed [{string.Join(",", allowed)}] but plan §3 wants [{string.Join(",", expectedValues)}]");
            }
        }

        foreach (var name in ExpectedOtherChecks.Where(name => !definitions.ContainsKey(name)))
        {
            problems.Add($"{name}: CHECK constraint is missing (plan §3)");
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ForeignKeys_UsePlanSection3DeleteAction()
    {
        var actual = SqlProbe.QueryStrings(
                Database,
                """
                SELECT cl.relname || '.' || a.attname || '|' || c.confdeltype::text
                FROM pg_constraint c
                JOIN pg_class cl ON cl.oid = c.conrelid
                JOIN pg_namespace n ON n.oid = cl.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey)
                WHERE c.contype = 'f' AND n.nspname = 'public'
                """)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        var described = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "NO ACTION",
            ["r"] = "RESTRICT",
            ["c"] = "CASCADE",
            ["n"] = "SET NULL",
        };

        foreach (var expected in ExpectedForeignKeys)
        {
            if (actual.Contains(expected))
            {
                continue;
            }

            var parts = expected.Split('|');
            var column = parts[0];
            var expectedAction = described[parts[1]];
            var found = actual.FirstOrDefault(entry =>
                entry.StartsWith(column + "|", StringComparison.Ordinal));

            problems.Add(found is null
                ? $"{column}: no foreign key constraint (plan §3 wants ON DELETE {expectedAction})"
                : $"{column}: {described[found.Split('|')[1]]} but plan §3 wants ON DELETE {expectedAction}");
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Indexes_MatchPlanSection3()
    {
        var actual = SqlProbe.QueryStrings(
                Database,
                """
                SELECT cl.relname || '|'
                       || (SELECT string_agg(
                                       pg_get_indexdef(i.indexrelid, k, true)
                                       || CASE WHEN (i.indoption[k - 1] & 1) = 1 THEN ' DESC' ELSE '' END,
                                       ', ' ORDER BY k)
                           FROM generate_series(1, i.indnkeyatts) AS k) || '|' || i.indisunique::text || '|'
                       || coalesce(pg_get_expr(i.indpred, i.indrelid), '')
                FROM pg_index i
                JOIN pg_class cl ON cl.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = cl.relnamespace
                WHERE n.nspname = 'public' AND cl.relkind = 'r'
                """)
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var problems = ExpectedIndexes
            .Where(expected => !actual.Contains(Normalize(expected)))
            .Select(expected => $"plan §3 index missing or different: {expected}")
            .ToList();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Assets_HaveTheRetentionDueIndex_AndUsersNoEmailIndex()
    {
        var indexes = SqlProbe.QueryStrings(Database, "SELECT indexname FROM pg_indexes WHERE schemaname = 'public'");

        indexes.ShouldContain("ix_assets_retention_due", "09.7.3-T1 adds the partial due-assets index (09 W3 CR-40)");
        indexes.ShouldNotContain("email_index", "the unique ux_users_normalized_email replaces it (09 W3 CR-41)");
        indexes.ShouldContain("ux_users_normalized_email");

        var definition = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'assets' "
                + "AND indexname = 'ix_assets_retention_due'")
            .ShouldHaveSingleItem();
        // A partial index over live, expiring rows only.
        Normalize(definition).ShouldEndWith(
            "USINGbtreeretention_untilWHEREdeleted_atISNULLANDretention_untilISNOTNULL");
    }

    /// <summary>
    /// Strips whitespace and the parentheses <c>pg_get_expr</c> adds, so the expected
    /// <c>archived_at IS NULL</c> compares equal to the catalog's <c>(archived_at IS NULL)</c>.
    /// </summary>
    private static string Normalize(string entry)
    {
        var parts = entry.Split('|');

        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = parts[index]
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("(", string.Empty, StringComparison.Ordinal)
                .Replace(")", string.Empty, StringComparison.Ordinal);
        }

        return string.Join('|', parts);
    }
}
