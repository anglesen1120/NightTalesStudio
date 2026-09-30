using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The migration harness of 09.2.2-T2: <c>InitialCore</c> reaches the latest migration on an empty
/// database, applies over the previous schema with data in it, and the model still agrees with the
/// snapshot it was generated from. CI runs the same tests in the <c>migrations</c> job, so a migration
/// that fails on a fresh database or leaves the model out of sync turns the run red.
/// </summary>
/// <remarks>
/// The two empty-database cases need a database that is *not* the run's template — one this test
/// creates without <c>TEMPLATE</c> and drops again — because a copy of the template is already
/// migrated. That is exactly what <c>nts-migrate</c> meets in a new environment (09.2.2 plan §7 UC1).
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class InitialCoreMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The 20 tables of the core set (plan §3) plus EF's own history table.</summary>
    private static readonly string[] ExpectedTables =
    [
        "__EFMigrationsHistory",
        "assets",
        "autopilot_runs",
        "episodes",
        "feature_flags",
        "idempotency_keys",
        "moderation_events",
        "outbox_messages",
        "role_claims",
        "roles",
        "series",
        "series_autopilot_authorizations",
        "story_versions",
        "style_templates",
        "usage_records",
        "user_claims",
        "user_logins",
        "user_roles",
        "user_tokens",
        "users",
        "voices",
    ];

    private PostgresFixture Fixture => Database.Fixture;

    [Fact]
    public async Task Migrations_ApplyOnEmptyDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Fixture;

        await InEmptyDatabaseAsync(async (name, connectionString) =>
        {
            await using var context = CreateContext(connectionString);

            (await context.Database.GetAppliedMigrationsAsync(ct))
                .ShouldBeEmpty("the database is created empty, with no __EFMigrationsHistory at all");

            await context.Database.MigrateAsync(ct);

            var compiled = CompiledMigrations(context);
            var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).Order(StringComparer.Ordinal).ToList();

            applied.ShouldBe(compiled, ignoreOrder: false, customMessage: "a fresh database must reach every compiled migration");
            (await context.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();

            (await fixture.TableNamesAsync(name, ct))
                .ShouldBe(ExpectedTables, ignoreOrder: false, customMessage: "the core set of plan §3 plus EF's history table");
        });
    }

    [Fact]
    public async Task Migrations_ApplyOnPreviousSchemaWithSeed()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Fixture;

        await InEmptyDatabaseAsync(async (name, connectionString) =>
        {
            await using var context = CreateContext(connectionString);

            // N-1: every compiled migration but the last one. With a single migration compiled, N-1 is
            // the empty schema and no `MigrateAsync` target exists to reach it (the task file's
            // "the test degenerates to apply-on-empty" case) — the branch below is the one a second
            // migration starts exercising, and it stays here so the harness is never silently skipped.
            var compiled = CompiledMigrations(context);
            var previous = compiled.Length > 1 ? compiled[^2] : null;

            if (previous is not null)
            {
                await context.Database.GetService<IMigrator>().MigrateAsync(previous, ct);
            }

            // Schema N-1 as the dataset sees it: the tables it has, with the columns they have now. The
            // seed writes one row for each of those tables, and both snapshots fingerprint exactly that
            // projection — so a column the last migration adds cannot enter the comparison.
            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, ct);
            var inheritedTables = inherited.Select(shape => shape.Table).ToList();

            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, ct);

            // The seed wrote exactly the dataset tables schema N-1 has. A dataset table N-1 lacks belongs
            // to the migration under test, and its insert joins the dataset only once it has shipped.
            seeded.ShouldBe(
                inheritedTables,
                "the seed writes a row for every dataset table schema N-1 has, and skips the tables the last "
                + "migration creates (their insert belongs to that migration)");

            var before = ToSnapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, ct));

            await context.Database.MigrateAsync(ct);

            // The data of the previous schema must come out of the last migration untouched: same row
            // count and same rows, for every table the seed wrote.
            var after = ToSnapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, ct));

            foreach (var table in inheritedTables)
            {
                after.ShouldContainKey(table);
                after[table].ShouldBe(before[table], customMessage: $"the last migration must not touch the rows of {table}");
            }

            (await context.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();

            var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).Order(StringComparer.Ordinal).ToList();
            applied.ShouldBe(compiled);

            // Every table the dataset names exists after the migration set has run: one the seed had to
            // skip above must have been created by it.
            var tablesAfter = await fixture.TableNamesAsync(name, ct);

            foreach (var table in SeedPrevious.Tables)
            {
                tablesAfter.ShouldContain(table, $"the migration set must leave every dataset table in place ({table})");
            }

            if (previous is null)
            {
                inheritedTables.ShouldBeEmpty(
                    "with one compiled migration the previous schema has no table of the seed dataset yet, so "
                    + "nothing is seeded; the moment a second migration is compiled N-1 is the whole InitialCore "
                    + "schema and the seed becomes the real data of N-1 (extend SeedPrevious.Dataset with the "
                    + "table a new feature migration adds)");
            }
            else
            {
                inheritedTables.ShouldNotBeEmpty(
                    "from the second migration on schema N-1 carries the core tables of the seed dataset, so "
                    + "the last migration is exercised against data and not against an empty schema");
            }
        });
    }

    [Fact]
    public async Task Model_HasNoPendingChanges()
    {
        await using var context = Database.CreateAppDbContext();

        // EF 10 `RelationalDatabaseFacadeExtensions.HasPendingModelChanges`: the model the contexts build
        // must be exactly the one the last migration's snapshot was generated from, or the next
        // `dotnet ef migrations add` would scaffold a migration for a change that was never made
        // (`dotnet ef migrations has-pending-model-changes` reports the same difference).
        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add` and commit the result)");
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a database created empty and dropped afterwards, so the
    /// migration cases never inherit the already-migrated template.
    /// </summary>
    private async Task InEmptyDatabaseAsync(Func<string, string, Task> body)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Fixture;

        // The template is built once per run; the class database and this scratch one both come from
        // the same server (and the same reserved `nts_` prefix rule).
        await fixture.EnsureReadyAsync();

        var name = PostgresFixture.RequireManagedDatabaseName($"nts_mig_empty_{Guid.CreateVersion7():N}");
        await fixture.CreateDatabaseAsync(name, templateDatabaseName: null, ct);

        try
        {
            await body(name, fixture.ConnectionStringFor(name));
        }
        finally
        {
            // Cleanup must run even when the test was cancelled, so it does not take the test token.
            await fixture.DropDatabaseAsync(name, CancellationToken.None);
        }
    }

    private static AppDbContext CreateContext(string connectionString) =>
        TestDbContextFactory.CreateAppContext(connectionString);

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint dictionary.</summary>
    private static Dictionary<string, string> ToSnapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);

    private static string[] CompiledMigrations(AppDbContext context) =>
        [.. context.Database.GetMigrations().Order(StringComparer.Ordinal)];
}
