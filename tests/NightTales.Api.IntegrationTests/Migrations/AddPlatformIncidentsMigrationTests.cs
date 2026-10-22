using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_06_PlatformIncidents</c> migration (06.2.3-T2, 06 README D14/D20, plan §3.3): the
/// <c>platform_incidents</c> table — the OAuth client the platform refused and the operator's clear — with the partial
/// unique index <c>ux_platform_incidents_open</c> that makes "one open incident per client" a storage fact, and the
/// newest-first <c>ix_platform_incidents_raised_at</c>.
/// </summary>
/// <remarks>
/// This class no longer owns the last-migration check (<c>migrations[^1]</c>): 02.8.3-T2's
/// <c>AddAutopilotAuthorizationRevokedReasonMigrationTests</c> appended its migration after this one and took that
/// check (and the no-pending-model-change check) over; this class now only proves its own migration is compiled in.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddPlatformIncidentsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "06_PlatformIncidents";

    private const string Table = "platform_incidents";

    private const string OpenIndexName = "ux_platform_incidents_open";

    /// <summary>The columns in <c>name|type|length|nullable</c> form: exactly D14's list, no audit and no row version.</summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "cleared_at|timestamp with time zone||YES",
        "cleared_by|text||YES",
        "error_code|text||NO",
        "id|uuid||NO",
        "note|text||YES",
        "oauth_client_id|text||NO",
        "platform|text||NO",
        "raised_at|timestamp with time zone||NO",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_platform_incidents_raised_at",
        "pk_platform_incidents",
        OpenIndexName,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsCompiledIn_AndMapsTheTable()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();

        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        context.Model.FindEntityType(typeof(PlatformIncident))!.GetTableName().ShouldBe(Table);
    }

    [Fact]
    public void Applies_WithTheColumnsKeyAndTheTwoIndexes()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + $"AND table_name = '{Table}' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint "
                + $"WHERE conrelid = '{Table}'::regclass AND contype = 'p'")
            .ShouldBe(["pk_platform_incidents|PRIMARY KEY (id)"]);

        SqlProbe.QueryStrings(
                Database,
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' "
                + $"AND tablename = '{Table}' ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);

        // The unique index is partial, which is what lets a client raise a new incident once the old one is cleared.
        var openIndex = SqlProbe.QueryStrings(
            Database,
            $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{OpenIndexName}'").Single();
        openIndex.ShouldContain("(platform, oauth_client_id)");
        openIndex.ShouldContain("WHERE (cleared_at IS NULL)");

        var raisedAtIndex = SqlProbe.QueryStrings(
            Database,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND indexname = 'ix_platform_incidents_raised_at'").Single();
        raisedAtIndex.ShouldContain("(raised_at DESC)");
    }

    [Fact]
    public void PlatformIncidents_RejectsASecondOpenRowForOneClient_AndAllowsOneAfterTheClear()
    {
        var client = "client-" + Guid.CreateVersion7().ToString("N");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        SqlProbe.Execute(Database, InsertSql(first, client));

        var duplicate = Should.Throw<PostgresException>(() => SqlProbe.Execute(Database, InsertSql(second, client)));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe(OpenIndexName);

        // Clearing the first row takes it out of the index's predicate, so the client can be raised again.
        SqlProbe.Execute(Database, $"UPDATE {Table} SET cleared_at = now(), cleared_by = 'seed', note = 'seed' "
            + $"WHERE id = '{first}'");
        SqlProbe.Execute(Database, InsertSql(second, client));

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE oauth_client_id = '{client}'").ShouldBe(2);
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE oauth_client_id = '{client}' AND cleared_at IS NULL")
            .ShouldBe(1);

        // A different client is a different incident, open at the same time.
        SqlProbe.Execute(Database, InsertSql(Guid.CreateVersion7(), client + "-other"));
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE cleared_at IS NULL AND oauth_client_id LIKE '{client}%'")
            .ShouldBe(2);
    }

    [Fact]
    public async Task Migration_HasOnlyCreateOperations()
    {
        await using var context = Database.CreateAppDbContext();
        var migration = CompiledMigration(context, MigrationName);

        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    [Fact]
    public async Task Down_DropsOnlyThePlatformIncidentTable_AndReApplies()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");
        var migrator = context.Database.GetService<IMigrator>();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);
            SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
                .ShouldBe("gone");
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.publications')::text, 'gone')")
                .ShouldNotBe("gone", "the Down of this migration must not touch the previous tables");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], Ct);
        }

        SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
            .ShouldNotBe("gone");
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table}").ShouldBe(0, "the recreated table is empty");

        // Re-applying the same migration is a no-op: the recreated table keeps its indexes and its predicate.
        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);
    }

    private static string InsertSql(Guid id, string client) =>
        $"INSERT INTO platform_incidents (id, platform, oauth_client_id, error_code, raised_at) "
        + $"VALUES ('{id}', 'youtube', '{client}', 'invalid_client', now())";

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }
}
