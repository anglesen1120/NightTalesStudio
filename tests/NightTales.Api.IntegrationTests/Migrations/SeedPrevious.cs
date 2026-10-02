using NightTales.TestKit.Database;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The dataset <c>Migrations_ApplyOnPreviousSchemaWithSeed</c> writes into schema N-1 *before* the
/// migration under test runs, so a migration that breaks existing data fails there instead of in
/// production (09.2.2-T2 by way of 02.2.2 plan L738).
/// </summary>
/// <remarks>
/// <para>
/// The rows are raw SQL, not the write model: the point is a schema that is one release behind the
/// model, which no current <c>AppDbContext</c> can express. Every insert must therefore stay valid for
/// the schema *before* the migration it guards — a column a later migration adds must not appear here
/// until that migration has shipped.
/// </para>
/// <para>
/// The dataset is the core set plan §3 promises 02 — one user, one series, two episodes, one story
/// version and one usage record, plus the two catalog rows the series row needs (a <c>voices</c> and a
/// <c>style_templates</c> row). A feature migration that adds a table (05.3.2-T3 <c>AddMusicTracks</c>,
/// 11.3.3-T1 <c>WorkerNodes</c>, …) extends <see cref="Dataset"/> with a row of its own table, so the
/// migration after it is exercised against data. With only <c>InitialCore</c> compiled, N-1 is the empty
/// schema, nothing is inserted, and the run degenerates to "apply on empty" — <see cref="ExecuteAsync"/>
/// skips a table the previous schema does not have instead of failing.
/// </para>
/// <para>
/// The same N-1 rule applies to the columns: <see cref="CaptureTablesAsync"/> records the columns each
/// table has *before* the migration runs, and <see cref="SnapshotAsync"/> hashes exactly that projection
/// on both sides. A migration that adds a column therefore does not disturb the comparison, while a
/// rewrite of an inherited column still does.
/// </para>
/// </remarks>
internal static class SeedPrevious
{
    /// <summary>One seed row: the table it needs, and an insert valid for the schema that has it.</summary>
    private static readonly (string Table, string Sql)[] Dataset =
    [
        (
            "voices",
            """
            INSERT INTO voices (id, display_name, language, gender, tags, active, created_at)
            VALUES ('seed-voice', 'Seed voice', 'vi', 'female', '{}', true, now())
            """),
        (
            "style_templates",
            """
            INSERT INTO style_templates (id, name, template, active, created_at)
            VALUES ('01920000-0000-7000-8000-000000000003', 'Seed template', 'Một câu chuyện về {term}.', true, now())
            """),
        (
            "users",
            """
            INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at,
                               email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled,
                               access_failed_count)
            VALUES ('01920000-0000-7000-8000-000000000001', 'Seed owner', 'vi', 'user', 'active', now(), now(),
                    true, false, false, false, 0)
            """),
        (
            "series",
            """
            INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id,
                                created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000001', 'Seed series',
                    'ghost', 'vi', 'landscape', 'seed-voice', '01920000-0000-7000-8000-000000000003', now(), now())
            """),
        (
            "episodes",
            """
            INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', 'Seed episode one', 'short', 'draft', now(), now()),
                   ('01920000-0000-7000-8000-000000000005', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', 'Seed episode two', 'standard', 'draft', now(), now())
            """),
        (
            "story_versions",
            """
            INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count,
                                        content, created_at)
            VALUES ('01920000-0000-7000-8000-000000000006', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', 1, 'generated', 'Seed story', 'Seed body', '{}', 2,
                    '{}'::jsonb, now())
            """),
        (
            "usage_records",
            """
            INSERT INTO usage_records (id, user_id, episode_id, series_id, provider, operation, input_tokens,
                                       cached_input_tokens, output_tokens, image_count, gpu_seconds, estimated,
                                       cost_usd, created_at)
            VALUES ('01920000-0000-7000-8000-000000000007', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000002', 'openai',
                    'story', 100, 0, 200, 0, 0, false, 0.05, now())
            """),
        (
            // 09.3.1-T1: the table the `_09_UserConsents` migration creates. The insert is skipped while that
            // migration is the last one (schema N-1 lacks the table) and runs once a later migration exists,
            // so every migration after it is exercised against a consent row.
            "user_consents",
            """
            INSERT INTO user_consents (id, user_id, document, version, accepted_at, ip)
            VALUES ('01920000-0000-7000-8000-000000000008', '01920000-0000-7000-8000-000000000001', 'tos', 'v1',
                    now(), '203.0.113.7')
            """),
        (
            // 09.3.2-T1: the table the `_09_RefreshTokens` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised against a live
            // session (a root row, still unrevoked, with the idle clock started).
            "refresh_tokens",
            """
            INSERT INTO refresh_tokens (id, user_id, family_id, token_hash, created_at, last_used_at, expires_at,
                                        reauth_attempts)
            VALUES ('01920000-0000-7000-8000-000000000009', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-00000000000a', 'seed-refresh-token-hash', now(), now(),
                    now() + interval '14 days', 0)
            """),
        (
            // 09.9.4-T1: the table the `_09_DataProtectionKeys` migration creates. Same rule as above:
            // skipped while that migration is the last one, then every later migration is exercised over a
            // key-ring row.
            "data_protection_keys",
            """
            INSERT INTO data_protection_keys (friendly_name, xml)
            VALUES ('seed-dp-key', '<key id="seed" />')
            """),
        (
            // 09.8.1-T2: the table the `_09_8_1_HubTickets` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a live,
            // unclaimed ticket row.
            "hub_tickets",
            """
            INSERT INTO hub_tickets (ticket_hash, user_id, created_at, expires_at)
            VALUES ('seed-hub-ticket-hash', '01920000-0000-7000-8000-000000000001', now(),
                    now() + interval '60 seconds')
            """),
        (
            // 09.2.6-T3: the table the `_09_EmailMessages` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a delivered
            // message (its `params` are already null, as a sent row is).
            "email_messages",
            """
            INSERT INTO email_messages (id, user_id, template, locale, to_hash, status, attempts, created_at,
                                        sent_at)
            VALUES ('01920000-0000-7000-8000-00000000000b', '01920000-0000-7000-8000-000000000001',
                    'password_changed', 'vi',
                    '9f2c1b0a7d6e5f40312a9b8c7d6e5f40312a9b8c7d6e5f40312a9b8c7d6e5f40', 'sent', 1, now(), now())
            """),
        (
            // 09.3.2-T3: the table the `_09_AuthChallenges` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a live
            // challenge row (both secrets stored hashed, as the store writes them).
            "auth_challenges",
            """
            INSERT INTO auth_challenges (id, user_id, token_hash, preauth_cookie_hash, user_agent, ip, attempts,
                                         expires_at, used_at)
            VALUES ('01920000-0000-7000-8000-00000000000c', '01920000-0000-7000-8000-000000000001',
                    'seed-mfa-token-hash', 'seed-preauth-cookie-hash', 'seed-agent', '203.0.113.9', 0,
                    now() + interval '5 minutes', NULL)
            """),
        (
            // 11.3.3-T1: the table the `_11_WorkerNodes` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a
            // registered node (an `https` row with a pin, the shape the seeder writes outside Development).
            "worker_nodes",
            """
            INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model,
                                      tls_cert_sha256, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-00000000000d', 'seed-node', 'https://node.example:8443', 2,
                    'active', now(), 'RTX 4090',
                    '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())
            """),
        (
            // 09.3.3-T1: the table the `_09_UserActionTokens` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over an
            // unspent reset token (only its SHA-256 hash is stored, as the store writes it).
            "user_action_tokens",
            """
            INSERT INTO user_action_tokens (id, user_id, kind, subject_id, token_hash, created_at, expires_at,
                                            used_at)
            VALUES ('01920000-0000-7000-8000-00000000000e', '01920000-0000-7000-8000-000000000001',
                    'password_reset', NULL,
                    '5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8', now(),
                    now() + interval '60 minutes', NULL)
            """),
        (
            // 02.2.2-T1: the table the `_02_GenerationRuns` migration creates. Same rule as above: skipped while
            // that migration is the last one, then every later migration is exercised over a finished story run
            // of the seed episode (queued, started and finished, so the time check and the failed/error rule
            // are exercised by real values).
            "generation_runs",
            """
            INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, "trigger", status, stage,
                                         previous_episode_status, counts_toward_limit, worst_case_usd, cost_usd,
                                         attempt, queued_at, started_at, finished_at, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-00000000000f', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000004', 'story',
                    'user', 'succeeded', 'done', 'draft', true, 0.250000, 0.120000, 0, now(), now(), now(),
                    now(), now())
            """),
    ];

    /// <summary>The tables the dataset covers, in insert order — the contract a feature migration extends.</summary>
    internal static IReadOnlyList<string> Tables => [.. Dataset.Select(row => row.Table)];

    /// <summary>
    /// The shape of the schema the dataset is written into: every dataset table it has, with the columns
    /// it has there, in ordinal order.
    /// </summary>
    /// <remarks>
    /// Captured against schema N-1, before the migration under test runs, and handed to both
    /// <see cref="SnapshotAsync"/> calls. A migration that adds a column therefore leaves the fingerprint
    /// of the inherited columns alone — which is the point: "the migration must not touch the rows" is a
    /// statement about the data that existed, not about columns the migration itself creates. A table the
    /// schema does not have yet is not captured: the insert for it belongs to the migration that creates
    /// it.
    /// </remarks>
    internal static async Task<IReadOnlyList<SeedTable>> CaptureTablesAsync(
        PostgresFixture fixture,
        string databaseName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var tables = new List<SeedTable>();

        foreach (var (table, _) in Dataset)
        {
            if (!await ExistsAsync(fixture, databaseName, table, ct))
            {
                continue;
            }

            var columns = await fixture.QueryStringsAsync(
                databaseName,
                "SELECT column_name FROM information_schema.columns "
                + $"WHERE table_schema = 'public' AND table_name = {Literal(table)} ORDER BY ordinal_position",
                ct);

            tables.Add(new SeedTable(table, columns));
        }

        return tables;
    }

    /// <summary>
    /// Inserts every dataset row whose table exists in the schema under test and returns the tables it
    /// actually wrote, in dataset order. A table the previous schema does not have yet is skipped: the
    /// insert for it belongs to the migration that creates it.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ExecuteAsync(
        PostgresFixture fixture,
        string databaseName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var seeded = new List<string>();

        foreach (var (table, sql) in Dataset)
        {
            if (!await ExistsAsync(fixture, databaseName, table, ct))
            {
                continue;
            }

            await fixture.ExecuteAsync(databaseName, sql, ct);
            seeded.Add(table);
        }

        return seeded;
    }

    /// <summary>
    /// One <c>table=count:md5</c> line per table of <paramref name="inherited"/>: the row count and a hash
    /// of the rows *as that schema projected them*. Taking this before and after the migration under test
    /// proves the migration did not disturb the data it inherited — a backfill that rewrites a row changes
    /// the hash, a delete changes the count, and a column the migration adds is not part of the
    /// fingerprint at all. The same <paramref name="inherited"/> shape must be passed to both calls: the
    /// after-side fingerprint hashes the same columns as the before-side one.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SnapshotAsync(
        PostgresFixture fixture,
        string databaseName,
        IReadOnlyList<SeedTable> inherited,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(inherited);

        var snapshot = new List<string>();

        foreach (var (table, columns) in inherited)
        {
            if (!await ExistsAsync(fixture, databaseName, table, ct))
            {
                // Only a drop reaches this, and it must fail the compare in the test rather than as an
                // SQL error: the before-side line for this table was `count:md5`, so the mismatch names
                // the table. The guard's DropTable check rejects such a migration as well.
                snapshot.Add($"{table}=dropped");
                continue;
            }

            var rows = await fixture.QueryStringsAsync(databaseName, FingerprintSql(table, columns), ct);

            snapshot.Add($"{table}={rows[0]}");
        }

        return snapshot;
    }

    /// <summary>
    /// The fingerprint query: the row count, and an md5 over the rows projected onto
    /// <paramref name="columns"/> in a stable order, so a column a later migration adds cannot enter the
    /// hash.
    /// </summary>
    private static string FingerprintSql(string table, IReadOnlyList<string> columns)
    {
        var projection = string.Join(", ", columns.Select(column => $"{Literal(column)}, t.{Identifier(column)}"));

        return "SELECT count(*)::text || ':' || coalesce(md5(string_agg(row_projection, '|' ORDER BY row_projection)), '') "
            + $"FROM (SELECT jsonb_build_object({projection})::text AS row_projection "
            + $"FROM {Identifier(table)} t) projected";
    }

    /// <summary>A SQL string literal; the values it quotes come from the catalog or from this file.</summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>A quoted SQL identifier; the values it quotes come from the catalog or from this file.</summary>
    private static string Identifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<bool> ExistsAsync(
        PostgresFixture fixture,
        string databaseName,
        string table,
        CancellationToken ct)
    {
        var registered = await fixture.QueryStringsAsync(
            databaseName,
            $"SELECT to_regclass('public.{table}')::text",
            ct);

        return registered.Count == 1 && registered[0] == table;
    }
}

/// <summary>
/// A seed table as the schema under test has it: its name, and its columns in ordinal order. The column
/// list is the projection <see cref="SeedPrevious.SnapshotAsync"/> fingerprints, captured before the
/// migration under test runs so an added column cannot change the comparison.
/// </summary>
/// <param name="Table">The table's name.</param>
/// <param name="Columns">The column names the schema has for it, in ordinal order.</param>
internal sealed record SeedTable(string Table, IReadOnlyList<string> Columns);
