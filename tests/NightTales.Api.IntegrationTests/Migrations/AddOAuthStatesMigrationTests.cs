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
/// The <c>_06_OAuthStates</c> migration (06.1.2-T1, 06 README D3/D20): the <c>oauth_states</c> table, one
/// single-use connect attempt per row, claimed by one atomic statement from an anonymous callback. <c>Up</c> is
/// expand-only — one <c>CreateTable</c>, its unique <c>state_hash</c> index and the
/// <c>(user_id, created_at DESC)</c> purge index, no other schema change — and <c>Down</c> drops the table and
/// nothing else. 06.1.2-T2 appended <c>_06_ChannelConnections</c> after it, so the newest migration's test
/// (<c>AddChannelConnectionsMigrationTests</c>) owns the last-migration check (<c>migrations[^1]</c>) and this
/// class finds its own migration by name.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddOAuthStatesMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "06_OAuthStates";

    private const string Table = "oauth_states";

    /// <summary>
    /// The columns in <c>name|type|length|nullable</c> form: exactly the task's SQL, with no <c>updated_at</c>
    /// and no row version — the row is never exposed with an ETag.
    /// </summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "browser_nonce_hash|text||NO",
        "code_verifier_protected|text||NO",
        "created_at|timestamp with time zone||NO",
        "expires_at|timestamp with time zone||NO",
        "id|uuid||NO",
        "oauth_client_id|text||NO",
        "platform|text||NO",
        "refresh_family_id|uuid||NO",
        "state_hash|text||NO",
        "used_at|timestamp with time zone||YES",
        "user_id|uuid||NO",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_oauth_states_user_created",
        "pk_oauth_states",
        "ux_oauth_states_state_hash",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsPresent_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        // 06.1.2-T2 appended a migration after this one, so this class no longer owns the last-migration
        // check (the newest migration's own test does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(OAuthState))!.GetTableName().ShouldBe(Table);
    }

    [Fact]
    public async Task Applies_WithTheColumnsCheckAndIndexes()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' "
                + $"AND table_name = '{Table}' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);

        SqlProbe.QueryStrings(
                Database,
                $"SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint "
                + $"WHERE conrelid = '{Table}'::regclass AND contype = 'p'")
            .ShouldBe([$"pk_{Table}|PRIMARY KEY (id)"]);

        // The closed platform set 06 and 07 write: a value outside it cannot be stored (asserted over a real
        // insert below), and this is the constraint that refuses it.
        SqlProbe.QueryStrings(
                Database,
                $"SELECT conname FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'c'")
            .ShouldBe([$"ck_{Table}_platform"]);

        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' "
                + "ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);

        // The purge index is ascending on `user_id`, its leading column, and descending on `created_at`: the
        // bounded delete walks the caller's newest rows first.
        SqlProbe.ScalarText(Database, $"SELECT pg_get_indexdef('ix_{Table}_user_created'::regclass)")
            .ShouldEndWith("(user_id, created_at DESC)");
    }

    [Fact]
    public void Schema_RejectsAnUnknownPlatform_AndRefusesAReplayedStateHash()
    {
        var unknownPlatform = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("seed-state-hash-a", "other")));
        unknownPlatform.SqlState.ShouldBe("23514");
        unknownPlatform.ConstraintName.ShouldBe($"ck_{Table}_platform");

        // `tiktok` is in the CHECK already, because 07 stores it in this same table.
        SqlProbe.Execute(Database, InsertSql("seed-state-hash-tiktok", "tiktok"));

        SqlProbe.Execute(Database, InsertSql("seed-state-hash-b", "youtube"));

        // One presented state resolves at most one row: the same `state_hash` can never be inserted twice.
        var replayed = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("seed-state-hash-b", "youtube")));
        replayed.SqlState.ShouldBe("23505");
        replayed.ConstraintName.ShouldBe($"ux_{Table}_state_hash");

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table}").ShouldBe(2);
    }

    [Fact]
    public async Task Migration_HasOnlyCreateOperations()
    {
        await using var context = Database.CreateAppDbContext();
        var migration = CompiledMigration(context, MigrationName);

        MigrationGuard.FindViolations([migration]).ShouldBeEmpty();
    }

    /// <summary>
    /// The schema one release behind, with the rows the seed harness writes into it (<see cref="SeedPrevious"/>):
    /// the inherited rows come out untouched and <c>oauth_states</c> joins them empty. The seed skips a table the
    /// previous schema does not have, so this run writes no state row of its own — the row of this table lands in
    /// the next migration's run.
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

            SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
                .ShouldBe("gone");

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldNotContain(Table, "the previous schema has no table to seed a state into");
            seeded.ShouldContain("users", "the migration must be exercised against data, not an empty schema");
            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            SqlProbe.Count(Database, $"SELECT count(*) FROM {Table}").ShouldBe(0);

            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsTheTableAndItsIndexes_AndReApplies()
    {
        // A state row of the schema the test starts from, so the Down is exercised over data.
        SqlProbe.Execute(Database, InsertSql("seed-state-hash-down", "youtube"));

        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
                .ShouldBe("gone", "Down drops the table");
            SqlProbe.Count(
                    Database,
                    $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}'")
                .ShouldBe(0, "the drop takes the table's indexes with it");
            SqlProbe.ScalarText(Database, "SELECT coalesce(to_regclass('public.platform_quota_usage')::text, 'gone')")
                .ShouldNotBe("gone", "the Down of this migration must not touch the sibling Publishing table");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        // Re-applied (NFR-09-6 expand-only), the table and its index set are back; only its own data is lost.
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table}").ShouldBe(0, "the recreated table is empty");
        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>
    /// One connect attempt as the connect command writes it: every column the table declares, with the state and
    /// the verifier unnamed beyond their column, so only the schema is under test.
    /// </summary>
    private static string InsertSql(string stateHash, string platform) =>
        $"INSERT INTO {Table} (id, user_id, platform, state_hash, code_verifier_protected, oauth_client_id, "
        + "refresh_family_id, browser_nonce_hash, created_at, expires_at) VALUES ("
        + $"'{Guid.CreateVersion7()}', '01920000-0000-7000-8000-000000000001', '{platform}', '{stateHash}', "
        + $"'seed-verifier-protected-aaaa', 'seed-client', '{Guid.CreateVersion7()}', 'seed-nonce-hash-aaaa', "
        + "now(), now() + interval '10 minutes')";

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
