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

/// <summary>The nullable approval timestamp expansion; this class owns the last-migration check.</summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class EpisodeApprovedAtMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_EpisodeApprovedAt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");
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
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1);
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[^2], Ct);
            SqlProbe.Execute(
                Database,
                "INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at) "
                + $"VALUES ('{episodeId}', '{user.Id}', '{series.Id}', 'Preexisting episode', 'short', 'draft', now(), now())");
            var before = await EpisodeDataAsync(episodeId);
            before.ShouldHaveSingleItem();

            await migrator.MigrateAsync(migrations[^1], Ct);

            (await Database.QueryStringsAsync(
                $"SELECT (approved_at IS NULL)::text FROM episodes WHERE id = '{episodeId}'", Ct)).ShouldBe(["true"]);
            (await EpisodeDataAsync(episodeId)).ShouldBe(before);
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
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
        migrations[^1].ShouldEndWith("_" + MigrationName, customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1);
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[^2], Ct);

            (await EpisodeColumnsAsync()).ShouldBe(columnsBefore.Where(column => column != "approved_at").ToList());
            (await EpisodeDataAsync(episode.Id)).ShouldBe(before);
            (await Database.QueryStringsAsync(
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", Ct)).ShouldBe(tablesBefore);

            await migrator.MigrateAsync(migrations[^1], Ct);

            (await EpisodeColumnsAsync()).ShouldBe(columnsBefore);
            (await EpisodeDataAsync(episode.Id)).ShouldBe(before);
            (await Database.QueryStringsAsync(
                $"SELECT (approved_at IS NULL)::text FROM episodes WHERE id = '{episode.Id}'", Ct)).ShouldBe(["true"]);
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    private Task<IReadOnlyList<string>> EpisodeDataAsync(Guid episodeId) => Database.QueryStringsAsync(
        $"SELECT (to_jsonb(e) - 'approved_at')::text FROM episodes e WHERE id = '{episodeId}'", Ct);

    private Task<IReadOnlyList<string>> EpisodeColumnsAsync() => Database.QueryStringsAsync(
        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'episodes' "
        + "ORDER BY ordinal_position", Ct);
}
