using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// NTS-922-SEC-001 (09 W3 CR-46, owner decision 2026-09-30, option (a)): the database itself refuses a
/// child row that points at another user's parent. Each same-owner foreign key of <c>InitialCore</c> is
/// composite — <c>(parent_id, user_id)</c> onto the parent's <c>(id, user_id)</c> alternate key — and a
/// nullable one nulls only the parent column on delete, never <c>user_id</c>.
/// </summary>
/// <remarks>
/// The negative cases write raw SQL on purpose: the point is that PostgreSQL rejects the row, so the
/// inserts must not go through a context that could fail earlier for another reason. The parent ids are
/// real rows of user A, and the insert carries user B, which is exactly the cross-tenant write the
/// application invariant of 09.2.3-T1 is the second control for.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class TenantForeignKeyTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// Every same-owner foreign key of the initial schema, in the form
    /// <c>child.parent_column,user_id-&gt;parent(id,user_id)</c>, ordered by child table.
    /// </summary>
    private static readonly string[] SameOwnerForeignKeys =
    [
        "assets.episode_id,user_id->episodes(id,user_id)",
        "autopilot_runs.episode_id,user_id->episodes(id,user_id)",
        "autopilot_runs.series_id,user_id->series(id,user_id)",
        "episodes.approved_story_version_id,user_id->story_versions(id,user_id)",
        "episodes.current_story_version_id,user_id->story_versions(id,user_id)",
        "episodes.final_video_asset_id,user_id->assets(id,user_id)",
        "episodes.series_id,user_id->series(id,user_id)",
        "fact_sheets.episode_id,user_id->episodes(id,user_id)",
        "fact_sheets.generation_run_id,user_id->generation_runs(id,user_id)",
        "fetch_tasks.episode_id,user_id->episodes(id,user_id)",
        "fetch_tasks.source_id,user_id->story_sources(id,user_id)",
        "generation_runs.episode_id,user_id->episodes(id,user_id)",
        "generation_runs.input_fact_sheet_id,user_id->fact_sheets(id,user_id)",
        "generation_runs.result_fact_sheet_id,user_id->fact_sheets(id,user_id)",
        "generation_runs.result_story_version_id,user_id->story_versions(id,user_id)",
        "generation_runs.series_id,user_id->series(id,user_id)",
        "moderation_events.episode_id,user_id->episodes(id,user_id)",
        "render_jobs.episode_id,user_id->episodes(id,user_id)",
        "render_jobs.story_version_id,user_id->story_versions(id,user_id)",
        "series.current_autopilot_authorization_id,user_id->series_autopilot_authorizations(id,user_id)",
        "series_autopilot_authorizations.series_id,user_id->series(id,user_id)",
        "series_bibles.proposed_from_episode_id,user_id->episodes(id,user_id)",
        "series_bibles.series_id,user_id->series(id,user_id)",
        "story_sources.episode_id,user_id->episodes(id,user_id)",
        "story_versions.episode_id,user_id->episodes(id,user_id)",
        "story_versions.fact_sheet_id,user_id->fact_sheets(id,user_id)",
        "story_versions.generation_run_id,user_id->generation_runs(id,user_id)",
        "story_versions.parent_version_id,user_id->story_versions(id,user_id)",
        "usage_records.episode_id,user_id->episodes(id,user_id)",
        "usage_records.generation_run_id,user_id->generation_runs(id,user_id)",
        "usage_records.series_id,user_id->series(id,user_id)",
    ];

    /// <summary>
    /// Every nullable <c>ON DELETE SET NULL</c> foreign key in the schema, in constraint-name order: the
    /// four same-owner ones, whose column list <c>InitialCore</c> writes with raw SQL because EF cannot emit
    /// it (a plain <c>SET NULL</c> would null <c>user_id</c> too and fail on the first delete of a parent),
    /// plus 09.3.2-T1's self-reference, which needs no column list and no same-owner rule.
    /// </summary>
    private static readonly string[] NullableSameOwnerDefinitions =
    [
        "fk_autopilot_runs_episodes_episode_id_user_id|FOREIGN KEY (episode_id, user_id) "
        + "REFERENCES episodes(id, user_id) ON DELETE SET NULL (episode_id)",
        "fk_episodes_assets_final_video_asset_id_user_id|FOREIGN KEY (final_video_asset_id, user_id) "
        + "REFERENCES assets(id, user_id) ON DELETE SET NULL (final_video_asset_id)",
        "fk_fact_sheets_generation_runs_generation_run_id_user_id|FOREIGN KEY (generation_run_id, user_id) "
        + "REFERENCES generation_runs(id, user_id) ON DELETE SET NULL (generation_run_id)",
        "fk_generation_runs_fact_sheets_input_fact_sheet_id_user_id|FOREIGN KEY (input_fact_sheet_id, user_id) "
        + "REFERENCES fact_sheets(id, user_id) ON DELETE SET NULL (input_fact_sheet_id)",
        "fk_generation_runs_fact_sheets_result_fact_sheet_id_user_id|FOREIGN KEY (result_fact_sheet_id, user_id) "
        + "REFERENCES fact_sheets(id, user_id) ON DELETE SET NULL (result_fact_sheet_id)",
        "fk_generation_runs_story_versions_result_story_version_id_user|FOREIGN KEY (result_story_version_id, "
        + "user_id) REFERENCES story_versions(id, user_id) ON DELETE SET NULL (result_story_version_id)",
        "fk_refresh_tokens_refresh_tokens_replaced_by_id|FOREIGN KEY (replaced_by_id) "
        + "REFERENCES refresh_tokens(id) ON DELETE SET NULL",
        "fk_series_bibles_episodes_proposed_from_episode_id_user_id|FOREIGN KEY "
        + "(proposed_from_episode_id, user_id) REFERENCES episodes(id, user_id) "
        + "ON DELETE SET NULL (proposed_from_episode_id)",
        "fk_series_series_autopilot_authorizations_current_autopilot_au|FOREIGN KEY "
        + "(current_autopilot_authorization_id, user_id) REFERENCES series_autopilot_authorizations(id, user_id) "
        + "ON DELETE SET NULL (current_autopilot_authorization_id)",
        "fk_story_versions_fact_sheets_fact_sheet_id_user_id|FOREIGN KEY (fact_sheet_id, user_id) "
        + "REFERENCES fact_sheets(id, user_id) ON DELETE SET NULL (fact_sheet_id)",
        "fk_story_versions_generation_runs_generation_run_id_user_id|FOREIGN KEY (generation_run_id, user_id) "
        + "REFERENCES generation_runs(id, user_id) ON DELETE SET NULL (generation_run_id)",
        "fk_story_versions_story_versions_parent_version_id_user_id|FOREIGN KEY (parent_version_id, user_id) "
        + "REFERENCES story_versions(id, user_id) ON DELETE SET NULL (parent_version_id)",
        "fk_usage_records_episodes_episode_id_user_id|FOREIGN KEY (episode_id, user_id) "
        + "REFERENCES episodes(id, user_id) ON DELETE SET NULL (episode_id)",
        "fk_usage_records_generation_runs_generation_run_id_user_id|FOREIGN KEY (generation_run_id, user_id) "
        + "REFERENCES generation_runs(id, user_id) ON DELETE SET NULL (generation_run_id)",
    ];

    /// <summary>
    /// One cross-tenant write: the label and an insert whose <c>user_id</c> belongs to user B while the
    /// parent reference belongs to user A. <c>{a}</c>, <c>{b}</c>, <c>{s_a}</c>, <c>{s_b}</c>,
    /// <c>{ep_a}</c>, <c>{ep_b}</c>, <c>{asset_a}</c>, <c>{version_a}</c>, <c>{version_b}</c>, <c>{run_a}</c>, <c>{fs_a}</c>,
    /// <c>{auth_a}</c>, <c>{template}</c>, <c>{voice}</c> and <c>{uid}</c> (a fresh unique token) are
    /// filled by the test.
    /// </summary>
    public static TheoryData<string, string> CrossTenantWrites => new()
    {
        {
            "episodes.series_id",
            "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), '{b}', '{s_a}', 'b', 'short', 'draft', now(), now())"
        },
        {
            "episodes.current_story_version_id",
            "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, current_story_version_id, "
            + "created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{s_b}', 'b', 'short', 'draft', "
            + "'{version_a}', now(), now())"
        },
        {
            "episodes.approved_story_version_id",
            "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, approved_story_version_id, "
            + "created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{s_b}', 'b', 'short', 'draft', "
            + "'{version_a}', now(), now())"
        },
        {
            "episodes.final_video_asset_id",
            "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, final_video_asset_id, "
            + "created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{s_b}', 'b', 'short', 'draft', "
            + "'{asset_a}', now(), now())"
        },
        {
            "story_versions.episode_id",
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES (gen_random_uuid(), '{b}', '{ep_a}', 2, 'generated', 't', "
            + "'b', '{}', 1, '{}'::jsonb, now())"
        },
        {
            "assets.episode_id",
            "INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, "
            + "created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{ep_a}', 'video', '{uid}', 'video/mp4', 1, "
            + "repeat('a', 64), now(), now())"
        },
        {
            "moderation_events.episode_id",
            "INSERT INTO moderation_events (id, user_id, episode_id, source, stage, categories, created_at) "
            + "VALUES (gen_random_uuid(), '{b}', '{ep_a}', 'internal', 'story', '{}', now())"
        },
        {
            "autopilot_runs.series_id",
            "INSERT INTO autopilot_runs (id, user_id, series_id, started_at, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), '{b}', '{s_a}', now(), now(), now())"
        },
        {
            "autopilot_runs.episode_id",
            "INSERT INTO autopilot_runs (id, user_id, series_id, started_at, episode_id, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), '{b}', '{s_a}', now(), '{ep_a}', now(), now())"
        },
        {
            "usage_records.episode_id",
            "INSERT INTO usage_records (id, user_id, episode_id, provider, operation, input_tokens, "
            + "cached_input_tokens, output_tokens, estimated, cost_usd, created_at) VALUES (gen_random_uuid(), "
            + "'{b}', '{ep_a}', 'openai', 'story', 1, 0, 1, false, 0.01, now())"
        },
        {
            "usage_records.series_id",
            "INSERT INTO usage_records (id, user_id, series_id, provider, operation, input_tokens, "
            + "cached_input_tokens, output_tokens, estimated, cost_usd, created_at) VALUES (gen_random_uuid(), "
            + "'{b}', '{s_a}', 'openai', 'story', 1, 0, 1, false, 0.01, now())"
        },
        {
            "series_autopilot_authorizations.series_id",
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, "
            + "topic, cadence, privacy_status, accepted_at, created_at, updated_at) VALUES (gen_random_uuid(), "
            + "'{b}', '{s_a}', 2, 'v1', repeat('a', 64), 'ghost', 'weekly', 'private', now(), now(), now())"
        },
        {
            "series.current_autopilot_authorization_id",
            "INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, "
            + "current_autopilot_authorization_id, created_at, updated_at) VALUES (gen_random_uuid(), '{b}', "
            + "'cross tenant', 'ghost', 'vi', 'landscape', '{voice}', '{template}', '{auth_a}', now(), now())"
        },
        {
            "generation_runs.series_id",
            "INSERT INTO generation_runs (id, user_id, series_id, kind, \"trigger\", status, stage, worst_case_usd, "
            + "queued_at, created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{s_a}', 'ideas', 'user', "
            + "'queued', 'queued', 0, now(), now(), now())"
        },
        {
            "generation_runs.episode_id",
            "INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, \"trigger\", status, stage, "
            + "worst_case_usd, queued_at, created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{s_b}', "
            + "'{ep_a}', 'story', 'user', 'cancelled', 'queued', 0, now(), now(), now())"
        },
        {
            "generation_runs.result_story_version_id",
            "INSERT INTO generation_runs (id, user_id, series_id, kind, \"trigger\", status, stage, worst_case_usd, "
            + "result_story_version_id, queued_at, created_at, updated_at) VALUES (gen_random_uuid(), '{b}', "
            + "'{s_b}', 'ideas', 'user', 'queued', 'queued', 0, '{version_a}', now(), now(), now())"
        },
        {
            "generation_runs.input_fact_sheet_id",
            "INSERT INTO generation_runs (id, user_id, series_id, kind, \"trigger\", status, stage, worst_case_usd, "
            + "input_fact_sheet_id, queued_at, created_at, updated_at) VALUES (gen_random_uuid(), '{b}', "
            + "'{s_b}', 'ideas', 'user', 'queued', 'queued', 0, '{fs_a}', now(), now(), now())"
        },
        {
            "generation_runs.result_fact_sheet_id",
            "INSERT INTO generation_runs (id, user_id, series_id, kind, \"trigger\", status, stage, worst_case_usd, "
            + "result_fact_sheet_id, queued_at, created_at, updated_at) VALUES (gen_random_uuid(), '{b}', "
            + "'{s_b}', 'ideas', 'user', 'queued', 'queued', 0, '{fs_a}', now(), now(), now())"
        },
        {
            "story_versions.generation_run_id",
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, generation_run_id, created_at) VALUES (gen_random_uuid(), '{b}', '{ep_b}', 1, "
            + "'generated', 't', 'b', '{}', 1, '{}'::jsonb, '{run_a}', now())"
        },
        {
            "story_versions.fact_sheet_id",
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, fact_sheet_id, created_at) VALUES (gen_random_uuid(), '{b}', '{ep_b}', 1, "
            + "'generated', 't', 'b', '{}', 1, '{}'::jsonb, '{fs_a}', now())"
        },
        {
            "story_versions.parent_version_id",
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, parent_version_id, created_at) VALUES (gen_random_uuid(), '{b}', '{ep_b}', 1, "
            + "'revised', 't', 'b', '{}', 1, '{}'::jsonb, '{version_a}', now())"
        },
        {
            "story_sources.episode_id",
            "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, "
            + "status, created_at) VALUES (gen_random_uuid(), '{b}', '{ep_a}', 'url', 'https://a.example/x', "
            + "'https://a.example/x', 'a.example', 'pending', now())"
        },
        {
            "fetch_tasks.episode_id",
            "WITH s AS (INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, "
            + "registrable_domain, status, created_at) VALUES (gen_random_uuid(), '{b}', '{ep_b}', 'url', "
            + "'https://b.example/x', 'https://b.example/x', 'b.example', 'pending', now()) RETURNING id) "
            + "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, "
            + "attempts, not_before, created_at) SELECT gen_random_uuid(), '{b}', '{ep_a}', s.id, 'fetch_source', "
            + "'https://b.example/x', 'b.example', 'queued', 0, now(), now() FROM s"
        },
        {
            "fetch_tasks.source_id",
            "WITH s AS (INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, "
            + "registrable_domain, status, created_at) VALUES (gen_random_uuid(), '{a}', '{ep_a}', 'url', "
            + "'https://a.example/y', 'https://a.example/y', 'a.example', 'pending', now()) RETURNING id) "
            + "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, "
            + "attempts, not_before, created_at) SELECT gen_random_uuid(), '{b}', '{ep_b}', s.id, 'fetch_source', "
            + "'https://a.example/y', 'a.example', 'queued', 0, now(), now() FROM s"
        },
        {
            "fact_sheets.episode_id",
            "INSERT INTO fact_sheets (id, user_id, episode_id, version, status, facts, source_aliases, "
            + "input_truncated, max_fact_id, content_sha256, prompt_version, created_by, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), '{b}', '{ep_a}', 2, 0, '[]'::jsonb, '{}'::jsonb, false, 0, "
            + "repeat('a', 64), 'user', '{b}', now(), now())"
        },
        {
            "fact_sheets.generation_run_id",
            "INSERT INTO fact_sheets (id, user_id, episode_id, version, status, facts, source_aliases, "
            + "input_truncated, max_fact_id, content_sha256, generation_run_id, prompt_version, created_by, "
            + "created_at, updated_at) VALUES (gen_random_uuid(), '{b}', '{ep_b}', 1, 0, '[]'::jsonb, "
            + "'{}'::jsonb, false, 0, repeat('a', 64), '{run_a}', 'user', '{b}', now(), now())"
        },
        {
            "render_jobs.episode_id",
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt) VALUES (gen_random_uuid(), '{b}', '{ep_a}', '{version_b}', "
            + "gen_random_uuid(), 'succeeded', 'done', 100, '{}'::jsonb, 1)"
        },
        {
            "render_jobs.story_version_id",
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt) VALUES (gen_random_uuid(), '{b}', '{ep_b}', '{version_a}', "
            + "gen_random_uuid(), 'succeeded', 'done', 100, '{}'::jsonb, 1)"
        },
    };

    [Theory]
    [MemberData(nameof(CrossTenantWrites))]
    public async Task CrossTenantChild_IsRejectedByTheDatabase(string column, string insert)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtures = await SeedAsync(ct);

        var sql = Fill(insert, fixtures);

        SqlProbe.SqlState(Database, sql)
            .ShouldBe("23503", $"{column} must refuse a child of user B pointing at user A's parent (NTS-922-SEC-001)");
    }

    [Fact]
    public async Task Episode_OfAnotherUser_PointingAtASeries_IsRejectedThroughTheWriteModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtures = await SeedAsync(ct);

        // The same attack through the application path: `EpisodeBuilder` writes through AppDbContext,
        // so this asserts that no amount of application-level mapping can slip the row past the rule.
        var episode = new EpisodeBuilder(Database, new TestCurrentUser());

        var failure = await Should.ThrowAsync<DbUpdateException>(
            () => episode.CreateAsync(fixtures.Intruder, fixtures.SeriesA, ct: ct));

        failure.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe("23503");
    }

    [Fact]
    public void SameOwnerForeignKeys_AreCompositeOntoTheAlternateKey()
    {
        // The complete set, not a sample: a new child table of a user-owned parent must appear here, and
        // the count is what keeps the rule from being applied table by table.
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT cl.relname || '.' || string_agg(a.attname, ',' ORDER BY k.ord) || '->'
                       || pl.relname || '(' || string_agg(pa.attname, ',' ORDER BY k.ord) || ')'
                FROM pg_constraint c
                JOIN pg_class cl ON cl.oid = c.conrelid
                JOIN pg_class pl ON pl.oid = c.confrelid
                JOIN unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
                JOIN pg_attribute a ON a.attrelid = cl.oid AND a.attnum = k.attnum
                JOIN unnest(c.confkey) WITH ORDINALITY AS p(attnum, ord) ON p.ord = k.ord
                JOIN pg_attribute pa ON pa.attrelid = pl.oid AND pa.attnum = p.attnum
                WHERE c.contype = 'f'
                      AND c.connamespace = 'public'::regnamespace
                      AND array_length(c.conkey, 1) > 1
                GROUP BY c.oid, cl.relname, pl.relname
                ORDER BY 1
                """)
            .ShouldBe(SameOwnerForeignKeys);
    }

    /// <summary>
    /// The other side of the same rule: the composite set above proves each of those foreign keys is
    /// composite, this proves no <em>additional</em> single-column foreign key to a user-owned parent
    /// exists next to one. Without it, a configuration that reverts a relationship to
    /// <c>HasForeignKey(child =&gt; child.ParentId)</c> while some other relationship keeps the composite
    /// key would leave the composite set looking complete and would reopen NTS-922-SEC-001 for that
    /// column. The only single-column keys the schema may carry into a user-owned parent are none:
    /// <c>user_id -&gt; users</c> points at <c>users</c>, which is not a user-owned parent here.
    /// </summary>
    [Fact]
    public void NoSingleColumnForeignKey_ReachesAUserOwnedParent()
    {
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT cl.relname || '.' || a.attname || '->' || pl.relname
                FROM pg_constraint c
                JOIN pg_class cl ON cl.oid = c.conrelid
                JOIN pg_class pl ON pl.oid = c.confrelid
                JOIN pg_attribute a ON a.attrelid = cl.oid AND a.attnum = c.conkey[1]
                WHERE c.contype = 'f'
                      AND c.connamespace = 'public'::regnamespace
                      AND array_length(c.conkey, 1) = 1
                      AND pl.relname IN ('series', 'episodes', 'assets', 'story_versions',
                                         'series_autopilot_authorizations', 'story_sources', 'fact_sheets')
                ORDER BY 1
                """)
            .ShouldBeEmpty();
    }

    [Fact]
    public void NullableSameOwnerForeignKeys_NullOnlyTheParentColumn()
    {
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT c.conname || '|' || pg_get_constraintdef(c.oid)
                FROM pg_constraint c
                WHERE c.contype = 'f'
                      AND c.connamespace = 'public'::regnamespace
                      AND pg_get_constraintdef(c.oid) LIKE '%SET NULL%'
                ORDER BY 1
                """)
            .ShouldBe(NullableSameOwnerDefinitions);
    }

    [Fact]
    public async Task Episode_Delete_KeepsTheUsageRecord_AndClearsOnlyItsEpisodeColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtures = await SeedAsync(ct);

        var record = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO usage_records (id, user_id, episode_id, provider, operation, input_tokens, "
            + $"cached_input_tokens, output_tokens, estimated, cost_usd, created_at) VALUES ('{record}', "
            + $"'{fixtures.Owner}', '{fixtures.EpisodeA}', 'openai', 'story', 100, 0, 200, false, 0.05, now())");

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{fixtures.EpisodeA}'");

        // Plan §8: the metered numbers survive the episode; only the reference to it is cleared, and the
        // owner column stays what it was (which is what the column-list SET NULL buys).
        var survivors = SqlProbe.QueryStrings(
            Database,
            $"SELECT user_id::text || '|' || coalesce(episode_id::text, 'null') FROM usage_records WHERE id = '{record}'");

        string[] expected = [$"{fixtures.Owner}|null"];
        survivors.ShouldBe(expected);
    }

    /// <summary>
    /// Two users with a full set of parents of user A (series, episode, asset, story version, autopilot
    /// authorization) and a series and an episode of user B: everything the cross-tenant cases need.
    /// </summary>
    private async Task<Fixtures> SeedAsync(CancellationToken ct)
    {
        var currentUser = new TestCurrentUser();
        var users = Database.CreateUserBuilder(currentUser);
        var owner = await users.CreateAsync(ct: ct);
        var intruder = await users.CreateAsync(ct: ct);

        var seriesBuilder = new SeriesBuilder(Database, currentUser);
        var seriesA = await seriesBuilder.CreateAsync(owner.Id, title: "Owner series", ct: ct);
        var seriesB = await seriesBuilder.CreateAsync(intruder.Id, title: "Intruder series", ct: ct);

        var episodeBuilder = new EpisodeBuilder(Database, currentUser);
        var episodeA = await episodeBuilder.CreateAsync(owner.Id, seriesA.Id, ct: ct);
        var episodeB = await episodeBuilder.CreateAsync(intruder.Id, seriesB.Id, ct: ct);

        // No builder exists for these three (their owning tasks come later), so they are raw rows with
        // the columns plan §3 marks NOT NULL.
        var assetA = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, "
            + $"created_at, updated_at) VALUES ('{assetA}', '{owner.Id}', '{episodeA.Id}', 'video', "
            + $"'{Guid.CreateVersion7():N}', 'video/mp4', 1024, repeat('a', 64), now(), now())");

        var versionA = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + $"word_count, content, created_at) VALUES ('{versionA}', '{owner.Id}', '{episodeA.Id}', 1, "
            + $"'generated', 'Seed story', 'Seed body', '{{}}', 2, '{{}}'::jsonb, now())");

        var versionB = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + $"word_count, content, created_at) VALUES ('{versionB}', '{intruder.Id}', '{episodeB.Id}', 2, "
            + $"'generated', 'Seed story', 'Seed body', '{{}}', 2, '{{}}'::jsonb, now())");

        var runA = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, \"trigger\", status, stage, "
            + $"worst_case_usd, queued_at, created_at, updated_at) VALUES ('{runA}', '{owner.Id}', '{seriesA.Id}', "
            + $"'{episodeA.Id}', 'story', 'user', 'queued', 'queued', 0, now(), now(), now())");

        var authorizationA = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + $"text_hash, topic, cadence, privacy_status, accepted_at, created_at, updated_at) VALUES "
            + $"('{authorizationA}', '{owner.Id}', '{seriesA.Id}', 1, 'v1', repeat('a', 64), 'ghost', 'weekly', "
            + $"'private', now(), now(), now())");

        // 02.5.3-T2: the fact sheet of user A that the three CR-29 cross-tenant cases point at.
        var factSheetA = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO fact_sheets (id, user_id, episode_id, version, status, facts, source_aliases, "
            + $"input_truncated, max_fact_id, content_sha256, prompt_version, created_by, created_at, updated_at) "
            + $"VALUES ('{factSheetA}', '{owner.Id}', '{episodeA.Id}', 1, 0, '[]'::jsonb, '{{}}'::jsonb, false, 0, "
            + $"repeat('a', 64), 'user', '{owner.Id}', now(), now())");

        var template = await seriesBuilder.EnsureStyleTemplateAsync(ct: ct);

        return new Fixtures(
            owner.Id,
            intruder.Id,
            seriesA.Id,
            seriesB.Id,
            episodeA.Id,
            episodeB.Id,
            assetA,
            versionA,
            versionB,
            runA,
            authorizationA,
            factSheetA,
            template.Id);
    }

    private static string Fill(string insert, Fixtures fixtures) => insert
        .Replace("{a}", fixtures.Owner.ToString(), StringComparison.Ordinal)
        .Replace("{b}", fixtures.Intruder.ToString(), StringComparison.Ordinal)
        .Replace("{s_a}", fixtures.SeriesA.ToString(), StringComparison.Ordinal)
        .Replace("{s_b}", fixtures.SeriesB.ToString(), StringComparison.Ordinal)
        .Replace("{ep_a}", fixtures.EpisodeA.ToString(), StringComparison.Ordinal)
        .Replace("{ep_b}", fixtures.EpisodeB.ToString(), StringComparison.Ordinal)
        .Replace("{run_a}", fixtures.GenerationRunA.ToString(), StringComparison.Ordinal)
        .Replace("{fs_a}", fixtures.FactSheetA.ToString(), StringComparison.Ordinal)
        .Replace("{asset_a}", fixtures.AssetA.ToString(), StringComparison.Ordinal)
        .Replace("{version_a}", fixtures.StoryVersionA.ToString(), StringComparison.Ordinal)
        .Replace("{version_b}", fixtures.StoryVersionB.ToString(), StringComparison.Ordinal)
        .Replace("{auth_a}", fixtures.AuthorizationA.ToString(), StringComparison.Ordinal)
        .Replace("{template}", fixtures.StyleTemplate.ToString(), StringComparison.Ordinal)
        .Replace("{voice}", SeriesBuilder.DefaultVoiceId, StringComparison.Ordinal)
        .Replace("{uid}", Guid.CreateVersion7().ToString("N"), StringComparison.Ordinal);

    /// <summary>The ids the cross-tenant inserts address.</summary>
    private sealed record Fixtures(
        Guid Owner,
        Guid Intruder,
        Guid SeriesA,
        Guid SeriesB,
        Guid EpisodeA,
        Guid EpisodeB,
        Guid AssetA,
        Guid StoryVersionA,
        Guid StoryVersionB,
        Guid GenerationRunA,
        Guid AuthorizationA,
        Guid FactSheetA,
        Guid StyleTemplate);
}
