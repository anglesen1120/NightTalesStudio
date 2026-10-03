using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_02_SourcesAndFetchTasks</c> migration (02.5.1-T1, 02.5.1 plan §3.2): both tables, their CHECKs and the index set
/// 02.5.2-T2's lease SQL depends on — above all the partial unique index of one live task per source and the same-owner
/// cascade keys. This class owns the last-migration check (<c>migrations[^1]</c>); older migration tests find their own
/// migration by name.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class SourcesAndFetchTasksMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_SourcesAndFetchTasks";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");

        context.Database.HasPendingModelChanges().ShouldBeFalse();
        context.Model.FindEntityType(typeof(StorySource))!.GetTableName().ShouldBe("story_sources");
        context.Model.FindEntityType(typeof(FetchTask))!.GetTableName().ShouldBe("fetch_tasks");
    }

    [Fact]
    public async Task Applies_WithTheFrozenIndexSet()
    {
        var indexes = await Database.QueryStringsAsync(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('story_sources', 'fetch_tasks') ORDER BY indexname",
            Ct);

        indexes.ShouldBe(
            [
                "ix_fetch_tasks_episode_id_user_id",
                "ix_fetch_tasks_leased_domain",
                "ix_fetch_tasks_leased_until",
                "ix_fetch_tasks_source_id_user_id",
                "ix_fetch_tasks_status_not_before",
                "ix_fetch_tasks_user_id",
                "ix_story_sources_episode_created",
                "ix_story_sources_episode_id_user_id",
                "ix_story_sources_episode_status",
                "ix_story_sources_user_url_created",
                "pk_fetch_tasks",
                "pk_story_sources",
                "ak_story_sources_id_user_id",
                "ux_fetch_tasks_live_source",
                "ux_story_sources_episode_url",
            ],
            ignoreOrder: true);

        var live = (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_fetch_tasks_live_source'", Ct)).ShouldHaveSingleItem();
        live.ShouldContain("UNIQUE");
        live.ShouldContain("source_id IS NOT NULL");
        live.ShouldContain("'queued'");
        live.ShouldContain("'leased'");

        (await Database.QueryStringsAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_story_sources_episode_url'", Ct))
            .ShouldHaveSingleItem().ShouldContain("(episode_id, normalized_url)");
    }

    [Theory]
    [InlineData("INSERT INTO story_sources (id, user_id, episode_id, kind, status, created_at) VALUES (gen_random_uuid(), '{u}', '{e}', 'url', 'pending', now())", "ck_story_sources_url")]
    [InlineData("INSERT INTO story_sources (id, user_id, episode_id, kind, status, created_at) VALUES (gen_random_uuid(), '{u}', '{e}', 'user_note', 'fetched', now())", "ck_story_sources_note")]
    [InlineData("INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, content_sha256, created_at) VALUES (gen_random_uuid(), '{u}', '{e}', 'url', 'u', 'u', 'a.b', 'pending', repeat('A', 64), now())", "ck_story_sources_sha256")]
    [InlineData("INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) VALUES (gen_random_uuid(), '{u}', '{e}', 'rss', 'u', 'u', 'a.b', 'pending', now())", "ck_story_sources_kind")]
    [InlineData("INSERT INTO story_sources (id, user_id, episode_id, kind, note_text, status, created_at) VALUES (gen_random_uuid(), '{u}', '{e}', 'user_note', repeat('x', 3001), 'fetched', now())", "ck_story_sources_note_len")]
    public async Task StorySources_Checks_ReportTheExactConstraint(string insert, string constraint)
    {
        var (user, episode) = await SeedEpisodeAsync();

        var failure = Should.Throw<Npgsql.PostgresException>(
            () => SqlProbe.Execute(Database, insert.Replace("{u}", user.ToString(), StringComparison.Ordinal).Replace("{e}", episode.ToString(), StringComparison.Ordinal)));

        failure.SqlState.ShouldBe("23514");
        failure.ConstraintName.ShouldBe(constraint);
    }

    [Fact]
    public async Task FetchTasks_OneLiveTaskPerSource_AndFetchSourceShapeIsChecked()
    {
        var (user, episode) = await SeedEpisodeAsync();
        var source = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
            + $"VALUES ('{source}', '{user}', '{episode}', 'url', 'https://a.example/', 'https://a.example/', 'a.example', 'pending', now())");

        string InsertTask(string status, string? extra = null) =>
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, attempts, not_before, created_at"
            + (extra is null ? ")" : ", lease_until, leased_by)")
            + $" VALUES (gen_random_uuid(), '{user}', '{episode}', '{source}', 'fetch_source', 'https://a.example/', 'a.example', '{status}', 0, now(), now()"
            + (extra is null ? ")" : $", {extra})");

        SqlProbe.Execute(Database, InsertTask("failed"));
        SqlProbe.Execute(Database, InsertTask("queued"));

        SqlProbe.SqlState(Database, InsertTask("queued")).ShouldBe("23505", "a second live task of one source");
        SqlProbe.SqlState(Database, InsertTask("leased", "now() + interval '90 seconds', 'fetcher-1'")).ShouldBe("23505");
        SqlProbe.SqlState(Database, InsertTask("leased")).ShouldBe("23514", "ck_fetch_tasks_leased: a lease needs lease_until and leased_by");
        SqlProbe.SqlState(
                Database,
                $"INSERT INTO fetch_tasks (id, user_id, episode_id, kind, status, attempts, not_before, created_at) VALUES (gen_random_uuid(), '{user}', '{episode}', 'fetch_source', 'queued', 0, now(), now())")
            .ShouldBe("23514", "ck_fetch_tasks_fetch_source: a fetch_source task needs source, url and domain");
    }

    [Fact]
    public async Task EpisodeDelete_CascadesSourcesAndTasks()
    {
        var (user, episode) = await SeedEpisodeAsync();
        var source = Guid.NewGuid();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status, created_at) "
            + $"VALUES ('{source}', '{user}', '{episode}', 'url', 'https://c.example/', 'https://c.example/', 'c.example', 'pending', now())");
        SqlProbe.Execute(
            Database,
            "INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status, attempts, not_before, created_at) "
            + $"VALUES (gen_random_uuid(), '{user}', '{episode}', '{source}', 'fetch_source', 'https://c.example/', 'c.example', 'queued', 0, now(), now())");

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{episode}'");

        (await Database.QueryStringsAsync($"SELECT (SELECT count(*) FROM story_sources WHERE episode_id = '{episode}')::text || ':' || (SELECT count(*) FROM fetch_tasks WHERE episode_id = '{episode}')::text", Ct))
            .ShouldBe(["0:0"]);
    }

    [Fact]
    public async Task DownMigration_DropsBothTables_AndUpRecreatesThem()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1);

        const string Probe = "SELECT coalesce(to_regclass('public.story_sources')::text, '') || '|' || coalesce(to_regclass('public.fetch_tasks')::text, '')";
        try
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);
            (await Database.QueryStringsAsync(Probe, Ct)).ShouldBe(["|"]);

            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^1], Ct);
            (await Database.QueryStringsAsync(Probe, Ct)).ShouldBe(["story_sources|fetch_tasks"]);
        }
        finally
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    /// <summary>One user with a series and an episode, written through the TestKit builders.</summary>
    private async Task<(Guid User, Guid Episode)> SeedEpisodeAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return (user.Id, episode.Id);
    }
}
