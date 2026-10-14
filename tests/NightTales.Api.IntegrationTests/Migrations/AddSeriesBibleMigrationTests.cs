using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Series.Bible;
using NightTales.Infrastructure.Persistence.Migrations;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

// Aliased: this assembly also declares the `NightTales.Api.IntegrationTests.SeriesBible` namespace (the
// endpoint tests of this feature), which would otherwise win over the Domain aggregate of the same name.
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_02_AddSeriesBible</c> migration (02.6.3-T1, plan §3.3): the <c>series_bibles</c> table with its
/// unique <c>series_id</c> index and its two jsonb documents, the same-owner composite key to <c>series</c>,
/// and the backfill that gives every series which predates the migration its one empty <c>bible.v1</c> row.
/// This class owns the last-migration check (<c>migrations[^1]</c>) and finds its own migration by name
/// everywhere else.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddSeriesBibleMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_AddSeriesBible";

    /// <summary>
    /// The document the backfill writes: the canonical JSON of <c>SeriesBibleContent.Empty</c> through
    /// <c>BibleJson</c>, the same bytes <c>BibleJson_EmptyContent_IsTheDocumentTheBackfillWrites</c> pins.
    /// </summary>
    private const string EmptyDocument =
        """{"schema_version":"bible.v1","narrator":{"name":null,"persona":"","voice_notes":""},"recurring_characters":[],"places":[],"rules":[],"episode_summaries":[]}""";

    private static readonly string[] ExpectedColumns =
    [
        "content", "created_at", "id", "proposed_content", "proposed_from_episode_id", "series_id", "updated_at",
        "user_id", "version",
    ];

    /// <summary>The columns whose type or nullability plan §3.3 fixes, in <c>name|type|length|nullable</c> form.</summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "content|jsonb||NO",
        "created_at|timestamp with time zone||NO",
        "id|uuid||NO",
        "proposed_content|jsonb||YES",
        "proposed_from_episode_id|uuid||YES",
        "series_id|uuid||NO",
        "updated_at|timestamp with time zone||NO",
        "user_id|uuid||NO",
        "version|integer||NO",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_series_bibles_user_id",
        "UX_series_bibles_series_id",
        "ix_series_bibles_proposed_from_episode_id",
        "ix_series_bibles_series_id_user_id",
        "pk_series_bibles",
    ];

    /// <summary>
    /// The three foreign keys of the table as PostgreSQL renders them: the composite same-owner key to
    /// <c>series(id, user_id)</c> (NTS-922-SEC-001), the episode a proposal came from (cleared, never cascaded)
    /// and the owner.
    /// </summary>
    private static readonly string[] ExpectedKeys =
    [
        "fk_series_bibles_asp_net_users_user_id|FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT",
        "fk_series_bibles_episodes_proposed_from_episode_id|FOREIGN KEY (proposed_from_episode_id) "
        + "REFERENCES episodes(id) ON DELETE SET NULL",
        "fk_series_bibles_series_series_id_user_id|FOREIGN KEY (series_id, user_id) "
        + "REFERENCES series(id, user_id) ON DELETE CASCADE",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The clock the Down test writes its row through the model with.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(SeriesBibleEntity))!.GetTableName().ShouldBe("series_bibles");
    }

    [Fact]
    public void Applies_WithTheColumnsAndTheirTypes()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'series_bibles' ORDER BY column_name")
            .ShouldBe(ExpectedColumns);

        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'series_bibles' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);
    }

    [Fact]
    public void Applies_WithTheFrozenIndexSet()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'series_bibles'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);

        // "One bible per series" is the database rule (the plan's unique index), not a handler convention.
        var seriesIndex = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'UX_series_bibles_series_id'")
            .ShouldHaveSingleItem();
        seriesIndex.ShouldContain("UNIQUE");
        seriesIndex.ShouldContain("(series_id)");

        var tenantIndex = SqlProbe.QueryStrings(
                Database,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + "AND indexname = 'IX_series_bibles_user_id'")
            .ShouldHaveSingleItem();
        tenantIndex.ShouldNotContain("UNIQUE");
        tenantIndex.ShouldContain("(user_id)");
    }

    [Fact]
    public void Applies_WithTheSameOwnerSeriesKeyAndTheEpisodeKey()
    {
        SqlProbe.QueryStrings(
                Database,
                """
                SELECT c.conname || '|' || pg_get_constraintdef(c.oid)
                FROM pg_constraint c
                WHERE c.conrelid = 'series_bibles'::regclass AND c.contype = 'f'
                ORDER BY 1
                """)
            .ShouldBe(ExpectedKeys);
    }

    /// <summary>
    /// The backfill: a series that existed before the migration comes out of it with exactly one row, and
    /// running the migration's own statement again changes nothing (the <c>WHERE NOT EXISTS</c> guard; without
    /// it the unique <c>series_id</c> index would raise 23505 on the second run).
    /// </summary>
    [Fact]
    public async Task Backfill_GivesEveryPreExistingSeriesOneRow_AndIsIdempotent()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            // Schema N-1: the table does not exist, so the series seeded here are exactly the "pre-existing"
            // ones the backfill has to find.
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.series_bibles')::text, 'gone')")
                .ShouldBe("gone");

            var actor = new TestCurrentUser();
            var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
            var first = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
            var second = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, "Second series", ct: Ct);

            await migrator.MigrateAsync(migrations[^1], Ct);

            var seriesCount = SqlProbe.Count(Database, "SELECT count(*) FROM series");
            seriesCount.ShouldBeGreaterThanOrEqualTo(2);
            SqlProbe.Count(Database, "SELECT count(*) FROM series_bibles")
                .ShouldBe(seriesCount, "the backfill writes the row of every series that predates the migration");
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM series s WHERE NOT EXISTS "
                    + "(SELECT 1 FROM series_bibles b WHERE b.series_id = s.id)")
                .ShouldBe(0, "no pre-existing series is left without its bible row");

            // The row is the create path's row: version 1, the owner's id, no proposal, and the canonical
            // empty document (jsonb equality, so key order and whitespace are the database's, not the writer's).
            SqlProbe.ScalarText(
                    Database,
                    $"SELECT version::text || '|' || (content = '{EmptyDocument}'::jsonb)::text || '|' "
                    + "|| (proposed_content IS NULL)::text || '|' "
                    + $"|| (proposed_from_episode_id IS NULL)::text || '|' || user_id::text "
                    + $"FROM series_bibles WHERE series_id = '{first.Id}'")
                .ShouldBe($"1|true|true|true|{user.Id}");

            RunBackfillStatementAgain();

            SqlProbe.Count(Database, "SELECT count(*) FROM series_bibles")
                .ShouldBe(seriesCount, "the second run inserts nothing: every series already has its row");
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM (SELECT series_id FROM series_bibles "
                    + "GROUP BY series_id HAVING count(*) > 1) duplicated")
                .ShouldBe(0);
            SqlProbe.ScalarText(Database, $"SELECT version::text FROM series_bibles WHERE series_id = '{second.Id}'")
                .ShouldBe("1");
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsTheTableAndItsIndexes_AndLeavesTheSeriesRows()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);

        await using (var write = Database.CreateAppDbContext(actor, new FakeClock(Start)))
        {
            write.SeriesBibles.Add(SeriesBibleEntity.CreateDefault(series.Id, user.Id, new FakeClock(Start)));
            await write.SaveChangesAsync(Ct);
        }

        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        var migrator = context.Database.GetService<IMigrator>();
        var seriesBefore = SqlProbe.Count(Database, "SELECT count(*) FROM series");

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.series_bibles')::text, 'gone')")
                .ShouldBe("gone", "Down drops the table");
            SqlProbe.Count(
                    Database,
                    "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' "
                    + "AND tablename = 'series_bibles'")
                .ShouldBe(0, "the drop takes the table's indexes with it");
            SqlProbe.Count(Database, "SELECT count(*) FROM series")
                .ShouldBe(seriesBefore, "the series rows the bible pointed at stay");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        // Re-applied, the table is back and the backfill gives every series its row again (01 §3.3 expand-only:
        // the migration is repeatable, and only the data of the table it owns is lost).
        SqlProbe.Count(Database, "SELECT count(*) FROM series_bibles")
            .ShouldBe(SqlProbe.Count(Database, "SELECT count(*) FROM series"));
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>
    /// Runs the migration's own raw-SQL step a second time. <c>UpOperations</c> is the generated <c>Up</c>, so
    /// this is the very statement the migration ran, not a copy of it.
    /// </summary>
    private void RunBackfillStatementAgain()
    {
        var statements = new _02_AddSeriesBible().UpOperations.OfType<SqlOperation>().ToList();
        statements.ShouldNotBeEmpty("the plan's backfill is the migration's raw SQL step");

        foreach (var statement in statements)
        {
            SqlProbe.Execute(Database, statement.Sql);
        }
    }
}
