using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_09_AddRenderJobWorstCaseUsd</c> migration (09.6.3-T1): <c>render_jobs.worst_case_usd</c>, the reserved
/// bound of an attempt, which the open-bounds reader sums with the generation runs' bound so the platform spend cap
/// covers a render in flight (02.2.2-T2, 03 D8). Up is expand-only — one <c>AddColumn</c>, <c>numeric(12,6)</c>
/// <c>NOT NULL DEFAULT 0</c>, no other schema change — and the default is what backfills the rows that predate the
/// column: render was not live in the release that added it, so an existing row is an attempt nobody was billed for
/// (an unconsumed bound is 0, never negative). <c>Down</c> drops the column and nothing else.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddRenderJobWorstCaseUsdMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "09_AddRenderJobWorstCaseUsd";

    /// <summary>The column, and the table it is added to.</summary>
    private const string Column = "worst_case_usd";

    private const string Table = "render_jobs";

    /// <summary>A bound of the stored scale (6 decimals), so no rounding can hide a wrong mapping.</summary>
    private const string BoundLiteral = "1.234567";

    /// <summary>
    /// The tables a migration <em>after</em> this one creates. The migrator is linear, so reverting to the
    /// migration before this one also reverts every later one and takes their tables with it: 06.1.2-T1's
    /// <c>_06_OAuthStates</c> appended <c>oauth_states</c> after this one and 06.1.2-T2's
    /// <c>_06_ChannelConnections</c> appended <c>channel_connections</c>.
    /// </summary>
    private static readonly string[] LaterMigrationTables = ["oauth_states", "channel_connections"];

    /// <summary>
    /// The <c>render_jobs</c> row of <see cref="SeedPrevious"/> (09.6.1-T1's dataset entry, the queued job of the
    /// seed story version): the row the migration is exercised against, and the one whose backfilled bound this
    /// class asserts.
    /// </summary>
    private const string SeededJobId = "01920000-0000-7000-8000-000000000017";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsPresent_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        // 06.1.2-T1 appended a migration after this one, so this class no longer owns the last-migration
        // check (the newest migration's own test does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");

        var property = context.Model.FindEntityType(typeof(RenderJob))!.FindProperty(nameof(RenderJob.WorstCaseUsd))!;
        property.ClrType.ShouldBe(typeof(decimal));
        property.IsNullable.ShouldBeFalse();
        property.GetColumnType().ShouldBe("numeric(12,6)");
        property.GetDefaultValue().ShouldBe(0m, "the store default is what backfills the rows that predate the column");
    }

    [Fact]
    public async Task Applies_WorstCaseUsdIsNumeric126_NotNull_WithZeroDefault()
    {
        (await ColumnShapeAsync()).ShouldBe(["numeric:NO:12:6"]);

        // The default is 0 for the rows the AddColumn backfills, and 0 is exactly what a job that omits the column
        // stores — the two paths agree, so no insert can produce a row outside the reader's non-negative sum.
        (await Database.QueryStringsAsync(
            "SELECT (column_default::numeric = 0)::text FROM information_schema.columns "
            + $"WHERE table_schema = 'public' AND table_name = '{Table}' AND column_name = '{Column}'",
            Ct)).ShouldBe(["true"]);

        var parents = await ParentsAsync();
        var job = InsertJobAtCurrentShape(parents, "queued");

        (await Database.QueryStringsAsync($"SELECT {Column}::text FROM {Table} WHERE id = '{job}'", Ct))
            .ShouldBe(["0.000000"]);
    }

    /// <summary>
    /// The schema one release behind, with the rows the seed harness writes into it (<see cref="SeedPrevious"/>):
    /// the pre-existing render job — the table's own seed row — comes out of the migration with the backfilled
    /// bound, and every other table keeps its rows byte for byte. The snapshot projection is the columns the
    /// previous schema had, so the added column cannot disturb the comparison.
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
            seeded.ShouldContain(Table, "the seed row of this table is what the migration is exercised against");
            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            // The seed job predates the column, so its bound is the default: the harness writes no worst_case_usd.
            (await Database.QueryStringsAsync(
                $"SELECT {Column}::text FROM {Table} WHERE id = '{SeededJobId}'",
                Ct)).ShouldBe(["0.000000"]);

            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    /// <summary>
    /// A job that exists before the migration keeps its row and every value, and gains the backfilled 0; the
    /// column lands with the stored scale in one statement, so the table is never left half-migrated.
    /// </summary>
    [Fact]
    public async Task Up_OnPreexistingJob_BackfillsZeroAndKeepsTheRow()
    {
        var parents = await ParentsAsync();
        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            (await ColumnShapeAsync()).ShouldBeEmpty("the previous schema has no worst_case_usd");

            var job = InsertJob(parents, "queued");
            var before = await JobDataAsync(job);

            await migrator.MigrateAsync(migrations[own], Ct);

            (await Database.QueryStringsAsync(
                $"SELECT ({Column} = 0 AND {Column} IS NOT NULL)::text FROM {Table} WHERE id = '{job}'", Ct))
                .ShouldBe(["true"], "an existing row gets the default 0, never null");
            (await JobDataAsync(job)).ShouldBe(before, "only the new column changes");

            // The bound of a new attempt is written by the command handler, not by the store default.
            SqlProbe.Execute(Database, $"UPDATE {Table} SET {Column} = {BoundLiteral} WHERE id = '{job}'");
            (await Database.QueryStringsAsync($"SELECT {Column}::text FROM {Table} WHERE id = '{job}'", Ct))
                .ShouldBe(["1.234567"]);
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsOnlyWorstCaseUsd_AndUpRestoresTheDefault()
    {
        var parents = await ParentsAsync();
        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);
        var columnsBefore = await ColumnsAsync();
        var tablesBefore = await Database.QueryStringsAsync(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", Ct);

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            (await ColumnsAsync()).ShouldBe(columnsBefore.Where(column => column != Column).ToList());
            (await ColumnShapeAsync()).ShouldBeEmpty();
            var tablesAfter = await Database.QueryStringsAsync(
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", Ct);

            // The migrator is linear: reverting to the migration before this one also reverts every later
            // one, so a table a later migration creates is gone with it. Every table that existed before
            // this migration — and this migration's own — must survive its Down.
            tablesAfter.ShouldBe(
                tablesBefore.Where(table => !LaterMigrationTables.Contains(table)).ToList(),
                "Down drops one column and no table");

            var job = InsertJob(parents, "queued");
            (await Database.QueryStringsAsync(
                $"SELECT (to_jsonb(j) - '{Column}')::text FROM {Table} j WHERE id = '{job}'", Ct))
                .ShouldHaveSingleItem();

            await migrator.MigrateAsync(migrations[own], Ct);

            (await ColumnsAsync()).ShouldBe(columnsBefore, "Up restores the column at the same shape");
            (await Database.QueryStringsAsync(
                $"SELECT ({Column} = 0)::text FROM {Table} WHERE id = '{job}'", Ct)).ShouldBe(["true"]);
            context.Database.HasPendingModelChanges().ShouldBeFalse();
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    /// <summary>One user with a series, an episode and one story version, written through the TestKit builders.</summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new Parents(user.Id, episode.Id, InsertVersion(user.Id, episode.Id));
    }

    /// <summary>
    /// The row the render command writes, without the column under test: the schema at N-1 has no
    /// <c>worst_case_usd</c>, so the insert must not name it — that omission is what the default backfills.
    /// </summary>
    private Guid InsertJob(Parents parents, string status)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO {Table} (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.EpisodeId}', '{parents.StoryVersionId}', "
            + $"'{Guid.CreateVersion7()}', '{status}', 'queued', 0, '{{}}'::jsonb, 1)");

        return id;
    }

    /// <summary>The same row at the current shape, with the column the model now always writes.</summary>
    private Guid InsertJobAtCurrentShape(Parents parents, string status)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO {Table} (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, " + Column + ") VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.EpisodeId}', '{parents.StoryVersionId}', "
            + $"'{Guid.CreateVersion7()}', '{status}', 'queued', 0, '{{}}'::jsonb, 1, 0)");

        return id;
    }

    private Guid InsertVersion(Guid userId, Guid episodeId)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES ("
            + $"'{id}', '{userId}', '{episodeId}', 1, 'generated', 'Seed title', 'Seed body', '{{}}', 2, "
            + "'{}'::jsonb, now())");

        return id;
    }

    /// <summary>The column's <c>type:nullable:precision:scale</c>, one row or none.</summary>
    private Task<IReadOnlyList<string>> ColumnShapeAsync() => Database.QueryStringsAsync(
        "SELECT data_type || ':' || is_nullable || ':' || coalesce(numeric_precision::text, '') || ':' "
        + "|| coalesce(numeric_scale::text, '') FROM information_schema.columns "
        + $"WHERE table_schema = 'public' AND table_name = '{Table}' AND column_name = '{Column}'",
        Ct);

    private Task<IReadOnlyList<string>> ColumnsAsync() => Database.QueryStringsAsync(
        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
        + $"AND table_name = '{Table}' ORDER BY column_name",
        Ct);

    /// <summary>Every column of the row except the one under test, so a migration that changes anything else fails.</summary>
    private Task<IReadOnlyList<string>> JobDataAsync(Guid jobId) => Database.QueryStringsAsync(
        $"SELECT (to_jsonb(j) - '{Column}')::text FROM {Table} j WHERE id = '{jobId}'", Ct);

    private static (IMigrator Migrator, List<string> Migrations, int Own) MigrationOf(AppDbContext context)
    {
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        return (context.Database.GetService<IMigrator>(), migrations, own);
    }

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint lookup.</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);

    private sealed record Parents(Guid UserId, Guid EpisodeId, Guid StoryVersionId);
}
