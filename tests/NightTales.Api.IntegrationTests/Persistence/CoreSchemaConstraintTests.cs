using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// The schema contract of the core set (09.2.2-T1, 09.2.2 plan §3): every enum column rejects a value
/// outside its <c>CHECK</c>, the email index is unique, the FKs delete the way plan §3 says, and a
/// column limit is enforced by the database, not by EF.
/// </summary>
/// <remarks>
/// The negative inserts use <c>gen_random_uuid()</c> for the FK columns: a PostgreSQL <c>CHECK</c> is
/// evaluated while the row is inserted, before the foreign-key triggers fire, so the SQLSTATE is always
/// the constraint's <c>23514</c> (or <c>22001</c>) and never an FK failure.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CoreSchemaConstraintTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>One enum column and a minimal insert that writes <c>'bogus'</c> into it.</summary>
    public static TheoryData<string, string> InvalidEnumInserts => new()
    {
        {
            "users.role",
            "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, "
            + "phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) VALUES "
            + "(gen_random_uuid(), 'x', 'vi', 'bogus', 'active', now(), now(), true, false, false, false, 0)"
        },
        {
            "users.status",
            "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, "
            + "phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) VALUES "
            + "(gen_random_uuid(), 'x', 'vi', 'user', 'bogus', now(), now(), true, false, false, false, 0)"
        },
        {
            "users.ui_locale",
            "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, "
            + "phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) VALUES "
            + "(gen_random_uuid(), 'x', 'bogus', 'user', 'active', now(), now(), true, false, false, false, 0)"
        },
        {
            "series.topic",
            "INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), 'x', 'bogus', 'vi', 'landscape', 'v', gen_random_uuid(), now(), now())"
        },
        {
            "series.language",
            "INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), 'x', 'ghost', 'bogus', 'landscape', 'v', gen_random_uuid(), now(), now())"
        },
        {
            "series.aspect",
            "INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), 'x', 'ghost', 'vi', 'bogus', 'v', gen_random_uuid(), now(), now())"
        },
        {
            "episodes.length_tier",
            "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'x', 'bogus', 'draft', now(), now())"
        },
        {
            "episodes.status",
            "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'x', 'short', 'bogus', now(), now())"
        },
        {
            "story_versions.source",
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, content, created_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, 'bogus', 't', 'b', '{}', 1, '{}'::jsonb, now())"
        },
        {
            "series_autopilot_authorizations.topic",
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, topic, cadence, "
            + "privacy_status, accepted_at, created_at, updated_at) VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, "
            + "'v1', repeat('a', 64), 'bogus', 'weekly', 'private', now(), now(), now())"
        },
        {
            "series_autopilot_authorizations.privacy_status",
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash, topic, cadence, "
            + "privacy_status, accepted_at, created_at, updated_at) VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 1, "
            + "'v1', repeat('a', 64), 'ghost', 'weekly', 'bogus', now(), now(), now())"
        },
        {
            "autopilot_runs.outcome",
            "INSERT INTO autopilot_runs (id, user_id, series_id, started_at, outcome, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now(), 'bogus', now(), now())"
        },
        {
            "moderation_events.source",
            "INSERT INTO moderation_events (id, user_id, source, stage, categories, created_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), 'bogus', 'draft', '{}', now())"
        },
        {
            "usage_records.operation",
            "INSERT INTO usage_records (id, user_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, "
            + "image_count, gpu_seconds, estimated, cost_usd, created_at) VALUES (gen_random_uuid(), gen_random_uuid(), 'openai', "
            + "'bogus', 0, 0, 0, 0, 0, false, 0, now())"
        },
        {
            "assets.kind",
            "INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'bogus', 'k', 'video/mp4', 1, repeat('a', 64), now(), now())"
        },
        {
            "voices.language",
            "INSERT INTO voices (id, display_name, language, gender, tags, active, created_at) VALUES ('v', 'v', 'bogus', 'female', '{}', true, now())"
        },
        {
            "voices.gender",
            "INSERT INTO voices (id, display_name, language, gender, tags, active, created_at) VALUES ('v', 'v', 'vi', 'bogus', '{}', true, now())"
        },
        {
            "style_templates.topic_hint",
            "INSERT INTO style_templates (id, name, template, topic_hint, active, created_at) "
            + "VALUES (gen_random_uuid(), 'n', 'a {term} b', 'bogus', true, now())"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidEnumInserts))]
    public void Constraints_RejectInvalidEnumValues(string column, string sql)
    {
        var sqlState = SqlProbe.SqlState(Database, sql);

        sqlState.ShouldBe(
            "23514",
            $"{column} must carry a CHECK constraint that rejects a value outside its enum (plan §3)");
    }

    [Fact]
    public void StyleTemplates_TemplateWithoutTerm_IsRejected()
    {
        var sqlState = SqlProbe.SqlState(
            Database,
            "INSERT INTO style_templates (id, name, template, active, created_at) VALUES (gen_random_uuid(), 'n', 'no placeholder', true, now())");

        sqlState.ShouldBe("23514");
    }

    [Fact]
    public void Users_NormalizedEmail_IsUnique()
    {
        var userId = Guid.CreateVersion7();

        SqlProbe.Execute(Database, InsertUser(userId, "dup@nighttales.test"));
        var sqlState = SqlProbe.SqlState(Database, InsertUser(Guid.CreateVersion7(), "dup@nighttales.test"));

        sqlState.ShouldBe("23505");
    }

    [Fact]
    public void Users_EmailIndex_IsReplacedByUniqueNormalizedEmail()
    {
        var indexes = SqlProbe.QueryStrings(
            Database,
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'users'");

        indexes.ShouldContain(definition =>
            definition.Contains("ux_users_normalized_email", StringComparison.Ordinal)
            && definition.Contains("UNIQUE", StringComparison.Ordinal)
            && definition.Contains("normalized_email", StringComparison.Ordinal));
        indexes.ShouldNotContain(definition => definition.Contains("email_index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Episodes_DeleteSeries_IsRestricted()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser()).CreateAsync(user.Id, ct: ct);
        await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(user.Id, series.Id, ct: ct);
        var sqlState = SqlProbe.SqlState(Database, $"DELETE FROM series WHERE id = '{series.Id}'");

        sqlState.ShouldBe("23503", "a series is archived, never deleted (plan §3)");
    }

    [Fact]
    public async Task Episodes_DeleteEpisode_CascadesStoryVersionsAssetsModeration()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser()).CreateAsync(user.Id, ct: ct);
        var episode = await new EpisodeBuilder(Database, new TestCurrentUser()).CreateAsync(user.Id, series.Id, ct: ct);

        var storyVersionId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var moderationId = Guid.CreateVersion7();
        var usageId = Guid.CreateVersion7();

        SqlProbe.Execute(
            Database,
            $"INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, content, created_at) "
            + $"VALUES ('{storyVersionId}', '{user.Id}', '{episode.Id}', 1, 'generated', 't', 'b', '{{}}', 1, '{{}}'::jsonb, now())");
        SqlProbe.Execute(
            Database,
            $"INSERT INTO assets (id, user_id, episode_id, kind, storage_key, content_type, size_bytes, sha256, created_at, updated_at) "
            + $"VALUES ('{assetId}', '{user.Id}', '{episode.Id}', 'video', 'k-{assetId:N}', 'video/mp4', 1, repeat('a', 64), now(), now())");
        SqlProbe.Execute(
            Database,
            $"INSERT INTO moderation_events (id, user_id, episode_id, source, stage, categories, created_at) "
            + $"VALUES ('{moderationId}', '{user.Id}', '{episode.Id}', 'internal', 'draft', '{{}}', now())");
        SqlProbe.Execute(
            Database,
            $"INSERT INTO usage_records (id, user_id, episode_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, "
            + $"image_count, gpu_seconds, estimated, cost_usd, created_at) VALUES ('{usageId}', '{user.Id}', '{episode.Id}', 'openai', 'story', "
            + "10, 0, 20, 0, 0, false, 0.5, now())");

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{episode.Id}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM story_versions WHERE id = '{storyVersionId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM assets WHERE id = '{assetId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM moderation_events WHERE id = '{moderationId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM usage_records WHERE id = '{usageId}'")
            .ShouldBe(1, "the metering row survives the episode deletion");
        SqlProbe.ScalarText(Database,
            $"SELECT coalesce(episode_id::text, 'null') FROM usage_records WHERE id = '{usageId}'")
            .ShouldBe("null", "usage_records.episode_id is ON DELETE SET NULL");
    }

    [Fact]
    public async Task Episodes_BriefOver1000_IsRejectedByColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var series = await new SeriesBuilder(Database, new TestCurrentUser()).CreateAsync(user.Id, ct: ct);
        var sqlState = SqlProbe.SqlState(
            Database,
            $"INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
            + $"VALUES (gen_random_uuid(), '{user.Id}', '{series.Id}', repeat('x', 1001), 'short', 'draft', now(), now())");

        sqlState.ShouldBe("22001");
    }

    [Fact]
    public async Task OutboxMessages_PayloadRoundTripsThroughJsonb()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var messageId = Guid.CreateVersion7();

        await using (var write = Database.CreateAppDbContext())
        {
            var message = new OutboxMessage
            {
                Id = messageId,
                Type = "episode_created.v1",
                Payload = """{"episodeId":"x"}""",
                UserId = user.Id,
                OccurredAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow,
            };

            write.OutboxMessages.Add(message);
            await write.SaveChangesAsync(ct);
        }

        await using (var read = Database.CreateAppDbContext())
        {
            var loaded = await read.OutboxMessages.SingleAsync(message => message.Id == messageId, ct);

            loaded.Payload.ShouldContain("episodeId");
            loaded.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5), "the column default stamps created_at");
            SqlProbe.ScalarText(Database, $"SELECT jsonb_typeof(payload)::text FROM outbox_messages WHERE id = '{messageId}'")
                .ShouldBe("object", "the string property maps to a jsonb column, not to text");
        }
    }

    private static string InsertUser(Guid id, string email) =>
        "INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, "
        + "phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, user_name, normalized_user_name, "
        + "email, normalized_email, concurrency_stamp) VALUES "
        + $"(gen_random_uuid(), 'x', 'vi', 'user', 'active', now(), now(), true, false, false, false, 0, '{email}', "
        + $"'{email.ToUpperInvariant()}', '{email}', '{email.ToUpperInvariant()}', '{id:N}')";
}
