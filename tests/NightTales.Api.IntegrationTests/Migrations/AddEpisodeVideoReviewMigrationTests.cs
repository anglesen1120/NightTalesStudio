using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Episodes;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_EpisodeVideoReview</c> migration (09.7.1-T1, 09 CR-48): the two nullable video-review columns of
/// <c>episodes</c> — <c>ready_at timestamptz</c> and <c>video_reject_reason varchar(500)</c>. <c>Up</c> is
/// expand-only (two <c>AddColumn</c>s, no data change, no backfill) and <c>Down</c> drops exactly those two
/// columns. The class owns the last-migration check for the new tip.
/// </summary>
/// <remarks>
/// A column-adding migration is proved the way <c>EpisodeApprovedAtMigrationTests</c> proves its own: the schema
/// one release behind carries the seed harness's rows, the hash covers only the columns that schema had, so the
/// comparison detects a rewrite of an inherited value while tolerating the two new columns.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddEpisodeVideoReviewMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "09_EpisodeVideoReview";

    private const string Table = "episodes";

    /// <summary>The second episode the seed harness writes, so "both seeded rows" is a real pair.</summary>
    private static readonly string[] SeededEpisodes =
    [
        "01920000-0000-7000-8000-000000000004",
        "01920000-0000-7000-8000-000000000005",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");

        var entity = context.Model.FindEntityType(typeof(Episode))!;
        entity.GetTableName().ShouldBe(Table);

        var readyAt = entity.FindProperty(nameof(Episode.ReadyAt))!;
        readyAt.ClrType.ShouldBe(typeof(DateTimeOffset?));
        readyAt.IsNullable.ShouldBeTrue("the column is nullable: an episode that was never approved has no instant");
        readyAt.GetColumnType().ShouldBe("timestamptz");
        readyAt.GetColumnName().ShouldBe("ready_at");

        var reason = entity.FindProperty(nameof(Episode.VideoRejectReason))!;
        reason.ClrType.ShouldBe(typeof(string));
        reason.IsNullable.ShouldBeTrue();
        reason.GetMaxLength().ShouldBe(500);
        reason.GetColumnName().ShouldBe("video_reject_reason");
    }

    [Fact]
    public async Task Applies_WithTwoNullableColumns_AndNoDefault()
    {
        (await Database.QueryStringsAsync(
                "SELECT column_name || ':' || data_type || ':' || coalesce(character_maximum_length::text, '<none>') "
                + "|| ':' || is_nullable || ':' || coalesce(column_default, '<none>') "
                + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'episodes' "
                + "AND column_name IN ('ready_at', 'video_reject_reason') ORDER BY column_name",
                Ct))
            .ShouldBe(
            [
                "ready_at:timestamp with time zone:<none>:YES:<none>",
                "video_reject_reason:character varying:500:YES:<none>",
            ]);
    }

    [Fact]
    public async Task Migration_HasOnlyAddOperations()
    {
        await using var context = Database.CreateAppDbContext();

        MigrationGuard.FindViolations([CompiledMigration(context, MigrationName)]).ShouldBeEmpty();
    }

    /// <summary>
    /// The schema one release behind with the seed harness's rows (<see cref="SeedPrevious"/>): every inherited
    /// value comes out untouched, and both new columns read <c>NULL</c> on the episodes that existed before the
    /// upgrade — the migration adds columns, it does not default or backfill them.
    /// </summary>
    [Fact]
    public async Task Applies_OnThePreviousSchemaWithSeededData()
    {
        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);
        var fixture = Database.Fixture;
        var name = Database.Name;

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldContain(Table, "the migration must be exercised against data, not an empty schema");
            (await ColumnsAsync()).ShouldNotContain("ready_at", "schema N-1 has neither column");
            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }

            var ids = string.Join(", ", SeededEpisodes.Select(id => $"'{id}'"));
            (await Database.QueryStringsAsync(
                    $"SELECT count(*)::text FROM {Table} WHERE id IN ({ids}) "
                    + "AND ready_at IS NULL AND video_reject_reason IS NULL",
                    Ct))
                .ShouldBe(["2"], "the pre-upgrade episodes survive with both new columns unset");
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsOnlyTheTwoColumns_PreservesEpisodeData_AndUpRestoresThem()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        var before = await EpisodeDataAsync(episode.Id);
        before.ShouldHaveSingleItem();
        var columnsBefore = await ColumnsAsync();
        columnsBefore.ShouldContain("ready_at");
        columnsBefore.ShouldContain("video_reject_reason");

        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            (await ColumnsAsync()).ShouldBe(
                columnsBefore.Where(column => column is not ("ready_at" or "video_reject_reason")).ToList(),
                "Down drops the two columns and nothing else");
            (await EpisodeDataAsync(episode.Id)).ShouldBe(before, "the episode keeps every other value");
            foreach (var table in new[] { Table, "render_jobs", "assets", "story_versions" })
            {
                SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{table}')::text, 'gone')")
                    .ShouldNotBe("gone", "the Down drops no table");
            }

            await migrator.MigrateAsync(migrations[own], Ct);

            (await ColumnsAsync()).ShouldBe(columnsBefore);
            (await EpisodeDataAsync(episode.Id)).ShouldBe(before);
            (await Database.QueryStringsAsync(
                    $"SELECT (ready_at IS NULL AND video_reject_reason IS NULL)::text FROM {Table} WHERE id = '{episode.Id}'",
                    Ct))
                .ShouldBe(["true"], "the recreated columns are empty, not filled in");
            context.Database.HasPendingModelChanges().ShouldBeFalse();
        }
        finally
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }
    }

    private Task<IReadOnlyList<string>> ColumnsAsync() => Database.QueryStringsAsync(
        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
        + $"AND table_name = '{Table}' ORDER BY ordinal_position",
        Ct);

    /// <summary>
    /// One episode's row with the two migrated columns removed, so the value comparison holds on both sides of
    /// the migration (where the columns do not exist yet) and on this side (where they are <c>NULL</c>).
    /// </summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The row as JSON, one line.</returns>
    private Task<IReadOnlyList<string>> EpisodeDataAsync(Guid episodeId) => Database.QueryStringsAsync(
        $"SELECT (to_jsonb(e) - 'ready_at' - 'video_reject_reason')::text FROM {Table} e WHERE id = '{episodeId}'",
        Ct);

    private static (IMigrator Migrator, List<string> Migrations, int Own) MigrationOf(AppDbContext context)
    {
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        return (context.Database.GetService<IMigrator>(), migrations, own);
    }

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint lookup.</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);
}
