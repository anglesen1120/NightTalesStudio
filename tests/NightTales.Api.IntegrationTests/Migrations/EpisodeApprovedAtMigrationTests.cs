using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>The nullable approval timestamp expansion.</summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class EpisodeApprovedAtMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_EpisodeApprovedAt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsApplied_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        // 02.5.3-T2 appended a migration after this one, so this class no longer owns the last-migration
        // check (the new migration's own test does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        context.Database.HasPendingModelChanges().ShouldBeFalse();

        var property = context.Model.FindEntityType(typeof(Episode))!.FindProperty(nameof(Episode.ApprovedAt))!;
        property.ClrType.ShouldBe(typeof(DateTimeOffset?));
        property.IsNullable.ShouldBeTrue();
        property.GetColumnType().ShouldBe("timestamptz");
    }

    [Fact]
    public async Task Applies_ApprovedAtIsNullableTimestampWithTimeZone_WithoutDefault()
    {
        (await Database.QueryStringsAsync(
            "SELECT data_type || ':' || is_nullable || ':' || coalesce(column_default, '<none>') "
            + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'episodes' "
            + "AND column_name = 'approved_at'",
            Ct)).ShouldBe(["timestamp with time zone:YES:<none>"]);
    }

    [Fact]
    public async Task Up_OnPreexistingEpisode_LeavesTimestampNullAndDataUnchanged()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episodeId = Guid.NewGuid();
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.Execute(
                Database,
                "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
                + $"VALUES ('{episodeId}', '{user.Id}', '{series.Id}', 'Preexisting episode', 'short', 'draft', now(), now())");
            var before = await EpisodeDataAsync(episodeId);
            before.ShouldHaveSingleItem();

            await migrator.MigrateAsync(migrations[own], Ct);

            (await Database.QueryStringsAsync(
                $"SELECT (approved_at IS NULL)::text FROM episodes WHERE id = '{episodeId}'", Ct)).ShouldBe(["true"]);
            (await EpisodeDataAsync(episodeId)).ShouldBe(before);
        }
        finally
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_RemovesOnlyApprovedAt_PreservesEpisodeData_AndUpRestoresNullColumn()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);
        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET approved_at = TIMESTAMPTZ '2026-10-05 08:00:00+00' WHERE id = '{episode.Id}'");
        (await Database.QueryStringsAsync(
            $"SELECT (approved_at = TIMESTAMPTZ '2026-10-05 08:00:00+00')::text FROM episodes WHERE id = '{episode.Id}'", Ct))
            .ShouldBe(["true"]);
        var before = await EpisodeDataAsync(episode.Id);
        before.ShouldHaveSingleItem();
        var columnsBefore = await EpisodeColumnsAsync();
        var tablesBefore = await Database.QueryStringsAsync(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", Ct);
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            // The migrator is linear: reverting to the migration before this one also reverts every later
            // one, so a table a later migration adds is gone with it. `fact_sheets` and `series_bibles` are the
            // two later migrations' tables (02_AddFactSheets, 02_AddSeriesBible), `render_jobs` is 09_RenderJobs
            // and `platform_quota_usage` is 06_PlatformQuotaUsage, `source_candidates` is 02_AddSourceCandidates,
            // `episode_ideas` is 02_AddEpisodeIdeas, `oauth_states` is 06_OAuthStates, `channel_connections`
            // is 06_ChannelConnections and `publications` is 06_Publications: all applied after
            // 02_EpisodeApprovedAt, so the loop skips them. Every table that existed before this migration must
            // survive its Down.
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            // 09_EpisodeVideoReview (09.7.1-T1) adds `ready_at` and `video_reject_reason` later: the linear Down
            // reverts them too, and the re-Up below stops at this migration, so neither column is expected.
            (await EpisodeColumnsAsync()).ShouldBe(
                columnsBefore.Where(column => column is not ("approved_at" or "ready_at" or "video_reject_reason")).ToList());
            (await EpisodeDataAsync(episode.Id)).ShouldBe(before);
            var tablesAfter = await Database.QueryStringsAsync(
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", Ct);
            foreach (var table in tablesBefore.Where(table
                => table is not ("fact_sheets" or "series_bibles" or "render_jobs" or "platform_quota_usage"
                    or "source_candidates" or "episode_ideas" or "oauth_states" or "channel_connections"
                    or "publications")))
            {
                tablesAfter.ShouldContain(table, "Down removes only approved_at; it must not drop a table");
            }

            await migrator.MigrateAsync(migrations[own], Ct);

            (await EpisodeColumnsAsync()).ShouldBe(
                columnsBefore.Where(column => column is not ("ready_at" or "video_reject_reason")).ToList());
            (await EpisodeDataAsync(episode.Id)).ShouldBe(before);
            (await Database.QueryStringsAsync(
                $"SELECT (approved_at IS NULL)::text FROM episodes WHERE id = '{episode.Id}'", Ct)).ShouldBe(["true"]);
        }
        finally
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }
    }

    private Task<IReadOnlyList<string>> EpisodeDataAsync(Guid episodeId) => Database.QueryStringsAsync(
        $"SELECT (to_jsonb(e) - 'approved_at' - 'ready_at' - 'video_reject_reason')::text FROM episodes e WHERE id = '{episodeId}'", Ct);

    private Task<IReadOnlyList<string>> EpisodeColumnsAsync() => Database.QueryStringsAsync(
        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'episodes' "
        + "ORDER BY ordinal_position", Ct);
}
