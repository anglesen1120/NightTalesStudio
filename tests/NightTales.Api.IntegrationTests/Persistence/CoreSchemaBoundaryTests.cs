using Microsoft.EntityFrameworkCore;
using NightTales.Domain.Platform;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// The boundary behaviour of the core schema (09.2.2-T1, 09.2.2 plan §3): column limits at N and N+1,
/// numeric precision overflow, the <c>NOT NULL</c> set, every <c>ON DELETE</c> action, the two circular
/// foreign keys, the audit-stamp precedence of <c>AuditableSaveChangesInterceptor</c> and the
/// <c>timestamptz</c> round-trip (09 W3 CR-38/-39/-40).
/// </summary>
/// <remarks>
/// The raw statements are deliberate: the point is that PostgreSQL itself rejects or accepts the row, so
/// EF's own validation can never mask a missing constraint. <see cref="Fill"/> substitutes
/// <c>{user}</c>, <c>{series}</c>, <c>{episode}</c>, <c>{voice}</c>, <c>{template}</c>, <c>{value}</c>
/// and <c>{uid}</c> (a fresh unique token per row).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CoreSchemaBoundaryTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>A column limit case: the label, the insert, and the length N its column allows.</summary>
    public static TheoryData<string, string, int> ColumnLimitCases => new()
    {
        { "users.display_name", InsertUserWithDisplayName("{value}"), 80 },
        { "users.email", InsertUserWithEmail("{value}"), 256 },
        { "series.title", InsertSeriesWithTitle("{value}"), 120 },
        { "episodes.title", InsertEpisodeWithTitle("{value}"), 120 },
        { "episodes.brief", InsertEpisodeWithBrief("{value}"), 1000 },
        { "assets.storage_key", InsertAssetWithStorageKey("{value}"), 512 },
        { "assets.sha256", InsertAssetWithSha256("{value}"), 64 },
        { "moderation_events.excerpt", InsertModerationEventWithExcerpt("{value}"), 300 },
        { "outbox_messages.error", InsertOutboxMessageWithError("{value}"), 2000 },
        { "idempotency_keys.key", InsertIdempotencyKeyWithKey("{value}"), 128 },
    };

    /// <summary>A numeric case: the label, the insert, a value that fits and one that overflows.</summary>
    public static TheoryData<string, string, string, string> NumericOverflowCases => new()
    {
        { "usage_records.cost_usd", InsertUsageWithCostUsd("{value}"), "999999.999999", "1000000.000000" },
        { "usage_records.gpu_seconds", InsertUsageWithGpuSeconds("{value}"), "99999999.99", "100000000.00" },
        { "assets.duration_sec", InsertAssetWithDuration("{value}"), "9999999.99", "10000000.00" },
        { "story_versions.similarity_score", InsertStoryVersionWithSimilarity("{value}"), "9.9999", "10.0000" },
    };

    /// <summary>One required column per core table, written as <c>NULL</c> where plan §3 says NOT NULL.</summary>
    public static TheoryData<string, string> RequiredColumnCases => new()
    {
        { "users.display_name", InsertUserWithDisplayName("NULL") },
        { "series.title", InsertSeriesWithTitle("NULL") },
        { "episodes.brief", InsertEpisodeWithBrief("NULL") },
        { "story_versions.content", InsertStoryVersionWithNullContent() },
        { "series_autopilot_authorizations.text_hash", InsertAuthorizationWithTextHash("NULL") },
        { "autopilot_runs.started_at", InsertAutopilotRunWithStartedAt("NULL") },
        { "moderation_events.source", InsertModerationEventWithNullSource() },
        { "usage_records.provider", InsertUsageWithNullProvider() },
        { "assets.sha256", InsertAssetWithSha256("NULL") },
        { "voices.display_name", InsertVoiceWithDisplayName("NULL") },
        { "style_templates.template", InsertStyleTemplateWithTemplate("NULL") },
        { "feature_flags.rules", InsertFeatureFlagWithRules("NULL") },
        { "outbox_messages.payload", InsertOutboxMessageWithNullPayload() },
        { "idempotency_keys.expires_at", InsertIdempotencyKeyWithNullExpiry() },
    };

    // ---------------------------------------------------------------- column limits

    [Theory]
    [MemberData(nameof(ColumnLimitCases))]
    public async Task ColumnLimits_AcceptN_RejectNPlusOne(string column, string insert, int limit)
    {
        _ = TestContext.Current.CancellationToken;
        var parents = await ParentsAsync();

        SqlProbe.Execute(Database, Fill(insert, parents, $"repeat('x', {limit})"));
        var sqlState = SqlProbe.SqlState(Database, Fill(insert, parents, $"repeat('x', {limit + 1})"));

        sqlState.ShouldBe("22001", $"{column} must reject {limit + 1} characters (plan §3)");
    }

    // ---------------------------------------------------------------- numeric precision

    [Theory]
    [MemberData(nameof(NumericOverflowCases))]
    public async Task NumericColumns_AcceptScale_RejectOverflow(
        string column,
        string insert,
        string accepted,
        string rejected)
    {
        var parents = await ParentsAsync();

        SqlProbe.Execute(Database, Fill(insert, parents, accepted));
        var sqlState = SqlProbe.SqlState(Database, Fill(insert, parents, rejected));

        sqlState.ShouldBe("22003", $"{column} must not store a value beyond its precision (plan §3)");
    }

    // ---------------------------------------------------------------- NOT NULL

    [Theory]
    [MemberData(nameof(RequiredColumnCases))]
    public async Task RequiredColumns_RejectNull(string column, string insert)
    {
        var parents = await ParentsAsync();

        var sqlState = SqlProbe.SqlState(Database, Fill(insert, parents, "NULL"));

        sqlState.ShouldBe("23502", $"{column} is NOT NULL in plan §3");
    }

    // ---------------------------------------------------------------- ON DELETE per plan §3

    [Fact]
    public async Task ForeignKeys_RestrictDeletingAReferencedVoiceTemplateUserAndSeries()
    {
        var parents = await ParentsAsync();

        SqlProbe.SqlState(Database, $"DELETE FROM voices WHERE id = '{parents.Voice}'")
            .ShouldBe("23503", "series.voice_id is ON DELETE RESTRICT (plan §3)");

        SqlProbe.SqlState(Database, $"DELETE FROM style_templates WHERE id = '{parents.Template}'")
            .ShouldBe("23503", "series.style_template_id is ON DELETE RESTRICT (plan §3)");

        SqlProbe.SqlState(Database, $"DELETE FROM users WHERE id = '{parents.User}'")
            .ShouldBe("23503", "series.user_id is ON DELETE RESTRICT (plan §3)");

        SqlProbe.SqlState(Database, $"DELETE FROM series WHERE id = '{parents.Series}'")
            .ShouldBe("23503", "episodes.series_id is ON DELETE RESTRICT: a series is archived, never deleted");
    }

    [Fact]
    public async Task ForeignKeys_NoActionBlocksDeletingAReferencedStoryVersion()
    {
        var parents = await ParentsAsync();
        var storyVersionId = InsertStoryVersionForEpisode(parents);

        var sqlState = SqlProbe.SqlState(Database, $"DELETE FROM story_versions WHERE id = '{storyVersionId}'");

        sqlState.ShouldBe("23503", "episodes.current_story_version_id is ON DELETE NO ACTION (plan §3)");
    }

    // ---------------------------------------------------------------- circular foreign keys

    [Fact]
    public async Task CircularForeignKeys_DeletingAnEpisodeCascadesItsCurrentStoryVersion()
    {
        var parents = await ParentsAsync();
        var storyVersionId = InsertStoryVersionForEpisode(parents);

        // The plan's failure-handling row: the episode delete must still cascade its versions even though
        // episodes.current_story_version_id points back at one of them.
        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{parents.Episode}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE id = '{storyVersionId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id = '{parents.Episode}'").ShouldBe(0);
    }

    [Fact]
    public async Task CircularForeignKeys_DeletingTheCurrentAuthorizationSetsTheSeriesColumnToNull()
    {
        var parents = await ParentsAsync();
        var authorizationId = Guid.CreateVersion7();

        SqlProbe.Execute(
            Database,
            $"INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, topic, "
            + $"cadence, privacy_status, accepted_at, created_at, updated_at) VALUES ('{authorizationId}', '{parents.User}', "
            + $"'{parents.Series}', 1, 'v1', repeat('a', 64), 'ghost', 'weekly', 'private', now(), now(), now())");
        SqlProbe.Execute(
            Database,
            $"UPDATE series SET current_autopilot_authorization_id = '{authorizationId}' WHERE id = '{parents.Series}'");

        // The authorizations hold the series (RESTRICT) while the series points at the current one.
        SqlProbe.SqlState(Database, $"DELETE FROM series WHERE id = '{parents.Series}'")
            .ShouldBe("23503", "series_autopilot_authorizations.series_id is ON DELETE RESTRICT (plan §3)");

        SqlProbe.Execute(Database, $"DELETE FROM series_autopilot_authorizations WHERE id = '{authorizationId}'");

        SqlProbe.ScalarText(
                Database,
                $"SELECT coalesce(current_autopilot_authorization_id::text, 'null') FROM series WHERE id = '{parents.Series}'")
            .ShouldBe("null", "series.current_autopilot_authorization_id is ON DELETE SET NULL (plan §3)");
    }

    [Fact]
    public async Task ForeignKeys_AllowDeletingAnUnreferencedVoice()
    {
        await ParentsAsync();

        SqlProbe.Execute(Database, "INSERT INTO voices (id, display_name, language, gender, tags, active, created_at) "
            + "VALUES ('qa-unreferenced-voice', 'v', 'vi', 'female', '{}', true, now())");
        SqlProbe.Execute(Database, "DELETE FROM voices WHERE id = 'qa-unreferenced-voice'");

        SqlProbe.Count(Database, "SELECT count(*) FROM voices WHERE id = 'qa-unreferenced-voice'").ShouldBe(0);
    }

    // ---------------------------------------------------------------- defaults

    [Fact]
    public async Task ColumnDefaults_ApplyWhenTheColumnIsOmitted()
    {
        var parents = await ParentsAsync();

        // users: ui_locale 'vi' and role 'user' (plan §3).
        SqlProbe.Execute(Database, "INSERT INTO users (id, display_name, status, created_at, updated_at, email_confirmed, "
            + "phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) "
            + "VALUES (gen_random_uuid(), 'qa-defaults-user', 'active', now(), now(), true, false, false, false, 0)");
        SqlProbe.ScalarText(Database, "SELECT ui_locale || ',' || role FROM users WHERE display_name = 'qa-defaults-user'")
            .ShouldBe("vi,user");

        // series: autopilot_enabled false.
        SqlProbe.Execute(
            Database,
            $"INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, created_at, updated_at) "
            + $"VALUES (gen_random_uuid(), '{parents.User}', 'qa-defaults-series', 'ghost', 'vi', 'landscape', '{parents.Voice}', '{parents.Template}', now(), now())");
        SqlProbe.ScalarText(Database, "SELECT autopilot_enabled::text FROM series WHERE title = 'qa-defaults-series'")
            .ShouldBe("false");

        // usage_records: input_tokens, cached_input_tokens, output_tokens, image_count and gpu_seconds
        // all default to 0 (plan §3), so the insert may omit them entirely.
        SqlProbe.Execute(
            Database,
            $"INSERT INTO usage_records (id, user_id, provider, operation, estimated, cost_usd, created_at) "
            + $"VALUES (gen_random_uuid(), '{parents.User}', 'openai', 'story', false, 0, now())");
        SqlProbe.ScalarText(
                Database,
                $"SELECT input_tokens::text || ',' || cached_input_tokens::text || ',' || output_tokens::text "
                + $"FROM usage_records WHERE user_id = '{parents.User}'")
            .ShouldBe("0,0,0");
        SqlProbe.ScalarText(
                Database,
                $"SELECT image_count::text || ',' || gpu_seconds::text FROM usage_records WHERE user_id = '{parents.User}'")
            .ShouldBe("0,0.00");

        // feature_flags: rules defaults to the empty object (plan §3).
        SqlProbe.Execute(Database, "INSERT INTO feature_flags (key, enabled, created_at, updated_at) "
            + "VALUES ('qa.defaults', true, now(), now())");
        SqlProbe.ScalarText(
                Database,
                "SELECT jsonb_typeof(rules)::text || ',' || rules::text FROM feature_flags WHERE key = 'qa.defaults'")
            .ShouldBe("object,{}");

        // outbox_messages: attempts 0 and created_at from the column default (the row is written inside
        // SaveChanges, after the audit interceptor, so it cannot rely on a CLR stamp).
        SqlProbe.Execute(Database, "INSERT INTO outbox_messages (id, type, payload, occurred_at, next_attempt_at) "
            + "VALUES (gen_random_uuid(), 'qa.v1', '{}'::jsonb, now(), now())");
        SqlProbe.ScalarText(
                Database,
                "SELECT attempts::text || ',' || (created_at IS NOT NULL)::text FROM outbox_messages WHERE type = 'qa.v1'")
            .ShouldBe("0,true");
    }

    // ---------------------------------------------------------------- unique index + NULLs

    [Fact]
    public async Task Users_MultipleNullNormalizedEmails_AreAllowed()
    {
        await ParentsAsync();

        // A unique index does not constrain NULLs: two users without an e-mail are legal while
        // ux_users_normalized_email still rejects a duplicate address (CoreSchemaConstraintTests).
        SqlProbe.Execute(Database, "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, "
            + "email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) "
            + "VALUES (gen_random_uuid(), 'qa-null-email-1', 'vi', 'user', 'active', now(), now(), false, false, false, false, 0)");
        SqlProbe.Execute(Database, "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, "
            + "email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) "
            + "VALUES (gen_random_uuid(), 'qa-null-email-2', 'vi', 'user', 'active', now(), now(), false, false, false, false, 0)");

        SqlProbe.Count(Database, "SELECT count(*) FROM users WHERE display_name IN ('qa-null-email-1', 'qa-null-email-2')")
            .ShouldBe(2);
    }

    // ---------------------------------------------------------------- audit interceptor

    [Fact]
    public async Task Timestamps_TimestamptzStoresTheInstantInUtc()
    {
        var parents = await ParentsAsync();
        var seriesId = Guid.CreateVersion7();

        // A value written with a +07:00 offset is one instant: the column stores it as UTC and the model
        // reads it back as the same instant with a zero offset (plan §3: timestamptz).
        SqlProbe.Execute(
            Database,
            $"INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, created_at, updated_at) "
            + $"VALUES ('{seriesId}', '{parents.User}', 'qa-timestamptz', 'ghost', 'vi', 'landscape', '{parents.Voice}', '{parents.Template}', "
            + "'2026-03-01 12:00:00+07'::timestamptz, '2026-03-01 12:00:00+07'::timestamptz)");

        ReadStamp("series", "created_at", $"id = '{seriesId}'").ShouldBe("2026-03-01T05:00:00");

        await using var read = Database.CreateAppDbContext();
        var loaded = await read.Series.SingleAsync(candidate => candidate.Id == seriesId, TestContext.Current.CancellationToken);

        loaded.CreatedAt.ShouldBe(new DateTimeOffset(2026, 3, 1, 5, 0, 0, TimeSpan.Zero));
        loaded.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task AuditableSaveChanges_AddedWithExplicitTimestamps_KeepsCreatedAtAndStampsUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var supplied = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var series = await new SeriesBuilder(Database, new TestCurrentUser(), clock)
            .CreateAsync(user.Id, createdAt: supplied, ct: ct);

        // 09 W3 CR-38 precedence: a non-default CreatedAt survives (a factory that already received `now`
        // keeps it), while UpdatedAt is always stamped from the clock.
        ReadStamp("series", "created_at", $"id = '{series.Id}'").ShouldBe("2026-03-01T12:00:00");
        ReadStamp("series", "updated_at", $"id = '{series.Id}'").ShouldBe("2026-01-01T00:00:00");

        await using var verify = Database.CreateAppDbContext(clock: clock);
        var loaded = await verify.Series.SingleAsync(candidate => candidate.Id == series.Id, ct);

        loaded.CreatedAt.ShouldBe(supplied);
        loaded.UpdatedAt.ShouldBe(clock.UtcNow);
    }

    [Fact]
    public async Task AuditableSaveChanges_NonUtcTimestamp_IsRejectedByTheProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var nonUtc = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(7));

        // 09 README D3: every timestamp is UTC (`IClock.UtcNow`). Npgsql only accepts an offset of zero
        // for `timestamp with time zone`, so a factory handing it a local-offset value fails at
        // SaveChanges instead of silently storing a shifted instant — the constraint is pinned here so a
        // future legacy-mode switch cannot re-enable implicit conversion unnoticed.
        var exception = await Should.ThrowAsync<DbUpdateException>(
            async () => await new SeriesBuilder(Database, new TestCurrentUser()).CreateAsync(user.Id, createdAt: nonUtc, ct: ct));

        exception.ToString().ShouldContain("only offset 0 (UTC) is supported");
    }

    [Fact]
    public async Task AuditableSaveChanges_ExecuteUpdateBypassesTheInterceptor()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var flag = FeatureFlag.Create($"qa_executeupdate_{Guid.CreateVersion7():N}");

        await using (var insert = Database.CreateAppDbContext(clock: clock))
        {
            insert.FeatureFlags.Add(flag);
            await insert.SaveChangesAsync(ct);
        }

        clock.Advance(TimeSpan.FromHours(2));

        await using (var update = Database.CreateAppDbContext(clock: clock))
        {
            var rows = await update.FeatureFlags
                .Where(candidate => candidate.Key == flag.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Enabled, true), ct);

            rows.ShouldBe(1);
        }

        // Documented consequence (09 W3 CR-38 / README D8): a set-based update is invisible to the
        // interceptor, so every such path must set UpdatedAt itself with SetProperty(…). The values are
        // read from PostgreSQL, so this is the stored state, not the tracked one.
        SqlProbe.ScalarText(Database, $"SELECT enabled::text FROM feature_flags WHERE key = '{flag.Key}'")
            .ShouldBe("true", "the set-based update itself took effect");
        ReadStamp("feature_flags", "updated_at", $"key = '{flag.Key}'")
            .ShouldBe("2026-01-01T00:00:00", "ExecuteUpdate bypasses AuditableSaveChangesInterceptor");
        ReadStamp("feature_flags", "created_at", $"key = '{flag.Key}'")
            .ShouldBe("2026-01-01T00:00:00", "the insert was stamped: only the set-based update is skipped");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Parents(Guid User, string Voice, Guid Template, Guid Series, Guid Episode);

    private static string Fill(string sql, Parents parents, string value) => sql
        .Replace("{user}", parents.User.ToString(), StringComparison.Ordinal)
        .Replace("{series}", parents.Series.ToString(), StringComparison.Ordinal)
        .Replace("{episode}", parents.Episode.ToString(), StringComparison.Ordinal)
        .Replace("{voice}", parents.Voice.ToString(), StringComparison.Ordinal)
        .Replace("{template}", parents.Template.ToString(), StringComparison.Ordinal)
        .Replace("{value}", value, StringComparison.Ordinal)
        .Replace("{uid}", $"qa-{Guid.CreateVersion7():N}", StringComparison.Ordinal);

    /// <summary>Reads one timestamp as UTC text for the rows matching <paramref name="where"/>.</summary>
    private string ReadStamp(string table, string column, string where) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS') FROM {table} WHERE {where}");

    /// <summary>
    /// A user with one voice, one style template, one series and one episode — the FK prerequisites of
    /// every raw statement below.
    /// </summary>
    private async Task<Parents> ParentsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: ct);
        var seriesBuilder = new SeriesBuilder(Database, actor);
        var voice = await seriesBuilder.EnsureVoiceAsync(ct: ct);
        var template = await seriesBuilder.EnsureStyleTemplateAsync(ct: ct);
        var series = await seriesBuilder.CreateAsync(user.Id, ct: ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: ct);

        return new Parents(user.Id, voice.Id, template.Id, series.Id, episode.Id);
    }

    /// <summary>Inserts a story_version of the episode and points the episode at it (the circular FK).</summary>
    private Guid InsertStoryVersionForEpisode(Parents parents)
    {
        var storyVersionId = Guid.CreateVersion7();

        SqlProbe.Execute(
            Database,
            $"INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, content, created_at) "
            + $"VALUES ('{storyVersionId}', '{parents.User}', '{parents.Episode}', 1, 'generated', 't', 'b', '{{}}', 1, '{{}}'::jsonb, now())");
        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET current_story_version_id = '{storyVersionId}' WHERE id = '{parents.Episode}'");

        return storyVersionId;
    }

    private static string InsertUserWithDisplayName(string displayName) =>
        "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, "
        + $"phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) VALUES "
        + $"(gen_random_uuid(), {displayName}, 'vi', 'user', 'active', now(), now(), true, false, false, false, 0)";

    private static string InsertUserWithEmail(string email) =>
        "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, "
        + "phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, user_name, "
        + $"normalized_user_name, email, normalized_email) VALUES "
        + $"(gen_random_uuid(), 'qa-email-limit', 'vi', 'user', 'active', now(), now(), true, false, false, false, 0, "
        + $"'{{uid}}', '{{uid}}', {email}, '{{uid}}')";

    private static string InsertSeriesWithTitle(string title) =>
        "INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', {title}, 'ghost', 'vi', 'landscape', '{{voice}}', '{{template}}', now(), now())";

    private static string InsertEpisodeWithTitle(string title) =>
        "INSERT INTO episodes (id, user_id, series_id, title, brief, length_tier, status, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{series}}', {title}, 'qa-brief', 'short', 'draft', now(), now())";

    private static string InsertEpisodeWithBrief(string brief) =>
        "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{series}}', {brief}, 'short', 'draft', now(), now())";

    private static string InsertAssetWithStorageKey(string storageKey) =>
        "INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{episode}}', 'video', {storageKey}, 'video/mp4', 1, repeat('a', 64), now(), now())";

    private static string InsertAssetWithSha256(string sha256) =>
        "INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{episode}}', 'video', '{{uid}}', 'video/mp4', 1, {sha256}, now(), now())";

    private static string InsertAssetWithDuration(string duration) =>
        "INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, duration_sec, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{episode}}', 'video', '{{uid}}', 'video/mp4', 1, repeat('a', 64), {duration}, now(), now())";

    private static string InsertModerationEventWithExcerpt(string excerpt) =>
        "INSERT INTO moderation_events (id, user_id, source, stage, categories, excerpt, created_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', 'internal', 'draft', '{{}}', {excerpt}, now())";

    private static string InsertModerationEventWithNullSource() =>
        "INSERT INTO moderation_events (id, user_id, source, stage, categories, created_at) "
        + "VALUES (gen_random_uuid(), '{user}', NULL, 'draft', '{}', now())";

    private static string InsertOutboxMessageWithError(string error) =>
        "INSERT INTO outbox_messages (id, type, payload, occurred_at, next_attempt_at, error) "
        + $"VALUES (gen_random_uuid(), 'qa.v1', '{{}}'::jsonb, now(), now(), {error})";

    private static string InsertOutboxMessageWithNullPayload() =>
        "INSERT INTO outbox_messages (id, type, payload, occurred_at, next_attempt_at) "
        + "VALUES (gen_random_uuid(), 'qa.v1', NULL, now(), now())";

    private static string InsertIdempotencyKeyWithKey(string key) =>
        "INSERT INTO idempotency_keys (id, user_id, key, request_hash, created_at, expires_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', {key}, repeat('a', 64), now(), now() + interval '24 hours')";

    private static string InsertIdempotencyKeyWithNullExpiry() =>
        "INSERT INTO idempotency_keys (id, user_id, key, request_hash, created_at, expires_at) "
        + "VALUES (gen_random_uuid(), '{user}', '{uid}', repeat('a', 64), now(), NULL)";

    private static string InsertUsageWithCostUsd(string cost) =>
        "INSERT INTO usage_records (id, user_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, "
        + $"estimated, cost_usd, created_at) VALUES (gen_random_uuid(), '{{user}}', 'openai', 'story', 0, 0, 0, false, {cost}, now())";

    private static string InsertUsageWithGpuSeconds(string gpuSeconds) =>
        "INSERT INTO usage_records (id, user_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, "
        + $"estimated, cost_usd, gpu_seconds, created_at) VALUES (gen_random_uuid(), '{{user}}', 'openai', 'story', 0, 0, 0, false, 0, {gpuSeconds}, now())";

    private static string InsertUsageWithNullProvider() =>
        "INSERT INTO usage_records (id, user_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, "
        + "estimated, cost_usd, created_at) VALUES (gen_random_uuid(), '{user}', NULL, 'story', 0, 0, 0, false, 0, now())";

    private static string InsertStoryVersionWithSimilarity(string similarity) =>
        "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, content, similarity_score, created_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{episode}}', 1, 'generated', 't', 'b', '{{}}', 1, '{{}}'::jsonb, {similarity}, now())";

    private static string InsertStoryVersionWithNullContent() =>
        "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, content, created_at) "
        + "VALUES (gen_random_uuid(), '{user}', '{episode}', 1, 'generated', 't', 'b', '{}', 1, NULL, now())";

    private static string InsertAuthorizationWithTextHash(string textHash) =>
        "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, topic, cadence, "
        + $"privacy_status, accepted_at, created_at, updated_at) VALUES (gen_random_uuid(), '{{user}}', '{{series}}', 1, 'v1', {textHash}, "
        + "'ghost', 'weekly', 'private', now(), now(), now())";

    private static string InsertAutopilotRunWithStartedAt(string startedAt) =>
        "INSERT INTO autopilot_runs (id, user_id, series_id, started_at, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), '{{user}}', '{{series}}', {startedAt}, now(), now())";

    private static string InsertVoiceWithDisplayName(string displayName) =>
        "INSERT INTO voices (id, display_name, language, gender, tags, active, created_at) "
        + $"VALUES ('{{uid}}', {displayName}, 'vi', 'female', '{{}}', true, now())";

    private static string InsertStyleTemplateWithTemplate(string template) =>
        "INSERT INTO style_templates (id, name, template, active, created_at) "
        + $"VALUES (gen_random_uuid(), '{{uid}}', {template}, true, now())";

    private static string InsertFeatureFlagWithRules(string rules) =>
        "INSERT INTO feature_flags (key, enabled, rules, created_at, updated_at) "
        + $"VALUES ('{{uid}}', true, {rules}, now(), now())";
}
