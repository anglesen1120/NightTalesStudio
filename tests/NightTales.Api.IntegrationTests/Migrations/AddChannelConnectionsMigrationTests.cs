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
/// The <c>_06_ChannelConnections</c> migration (06.1.2-T2, 06 README D3/D4/D18/D20): the
/// <c>channel_connections</c> table, one owned publishable channel per row, both tokens as
/// <c>ITokenProtector</c> ciphertext and the OAuth client that minted the code pinned on the row.
/// <c>Up</c> is expand-only — one <c>CreateTable</c>, its tenant index, the partial unique index
/// <c>ux_channel_connections_account</c> and the two <c>CHECK</c>s, no other schema change — and
/// <c>Down</c> drops the table and nothing else. The class owns the last-migration check for the new tip.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddChannelConnectionsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "06_ChannelConnections";

    private const string Table = "channel_connections";

    /// <summary>The connect path writes one live row per channel; there is no FK, so any uuid is a valid owner.</summary>
    private const string Owner = "01920000-0000-7000-8000-000000000001";

    /// <summary>The two-URI scope the connect path stores, as Google returns it.</summary>
    private const string Scope =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>
    /// The columns in <c>name|type|length|nullable</c> form: exactly the task's SQL. The two token columns
    /// are nullable because <c>Revoke</c> nulls them, <c>external_account_id</c>/<c>display_name</c> because
    /// <c>PurgeYouTubeData</c> nulls them outright rather than writing a placeholder, and the table has no
    /// <c>updated_at</c> and no row version — this resource carries no ETag.
    /// </summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "access_token_enc|text||YES",
        "connected_at|timestamp with time zone||NO",
        "display_name|character varying|200|YES",
        "external_account_id|character varying|64|YES",
        "id|uuid||NO",
        "last_verified_at|timestamp with time zone||YES",
        "oauth_client_id|character varying|128|NO",
        "platform|text||NO",
        "refresh_token_enc|text||YES",
        "revoked_at|timestamp with time zone||YES",
        "scope|character varying|512|NO",
        "status|text||NO",
        "token_expires_at|timestamp with time zone||NO",
        "user_id|uuid||NO",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_channel_connections_user_id",
        "pk_channel_connections",
        "ux_channel_connections_account",
    ];

    private static readonly string[] ExpectedCheckConstraints =
    [
        "ck_channel_connections_platform",
        "ck_channel_connections_status",
    ];

    /// <summary>The four wire values of <c>ChannelConnectionStatus</c>: the closed set the status <c>CHECK</c> pins.</summary>
    private static readonly string[] Statuses = ["active", "error", "expired", "revoked"];

    /// <summary>The two values <c>ck_channel_connections_platform</c> accepts: 06's platform and 07's.</summary>
    private static readonly string[] Platforms = ["tiktok", "youtube"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
        context.Model.FindEntityType(typeof(ChannelConnection))!.GetTableName().ShouldBe(Table);
    }

    [Fact]
    public void Applies_WithTheColumnsChecksAndIndexes()
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

        SqlProbe.QueryStrings(
                Database,
                $"SELECT conname FROM pg_constraint WHERE conrelid = '{Table}'::regclass "
                + "AND contype = 'c' ORDER BY conname")
            .ShouldBe(ExpectedCheckConstraints);

        // The platform CHECK carries 07's `tiktok` before the enum declares it (OAuthStateConfiguration's
        // pair, 07 CR-13) and the status CHECK exactly the four lower-case wire values: compared by the
        // values they carry, never against the literal the model wrote (`IN` becomes `= ANY (ARRAY[…])`).
        var platformCheck = SqlProbe.QueryStrings(
                Database,
                $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{Table}'::regclass "
                + "AND conname = 'ck_channel_connections_platform'")
            .ShouldHaveSingleItem();
        foreach (var platform in Platforms)
        {
            platformCheck.ShouldContain($"'{platform}'");
        }

        var statusCheck = SqlProbe.QueryStrings(
                Database,
                $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{Table}'::regclass "
                + "AND conname = 'ck_channel_connections_status'")
            .ShouldHaveSingleItem();
        foreach (var status in Statuses)
        {
            statusCheck.ShouldContain($"'{status}'");
        }

        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' "
                + "ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);

        // The two indexes are README D18 in full: the tenant list index is plain and covers every status,
        // and the account index is the partial one — its `WHERE` predicate, not a NULLS clause, is what keeps
        // the revoked historical row out of the key.
        var tenantIndex = SqlProbe.QueryStrings(
                Database,
                $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' "
                + "AND indexname = 'ix_channel_connections_user_id'")
            .ShouldHaveSingleItem();
        tenantIndex.ShouldNotContain("UNIQUE");
        tenantIndex.ShouldNotContain("WHERE");
        tenantIndex.ShouldContain("(user_id)");

        var accountIndex = SqlProbe.QueryStrings(
                Database,
                $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' "
                + "AND indexname = 'ux_channel_connections_account'")
            .ShouldHaveSingleItem();
        accountIndex.ShouldContain("UNIQUE");
        accountIndex.ShouldContain("(platform, external_account_id)");
        accountIndex.ShouldContain("status <> 'revoked'");
    }

    [Fact]
    public void Schema_RejectsAnUnknownPlatformAndStatus_AndKeepsNoForeignKeyToUsers()
    {
        var unknownPlatform = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("other", "active", "chan-platform")));
        unknownPlatform.SqlState.ShouldBe("23514");
        unknownPlatform.ConstraintName.ShouldBe($"ck_{Table}_platform");

        var unknownStatus = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("youtube", "pending", "chan-status")));
        unknownStatus.SqlState.ShouldBe("23514");
        unknownStatus.ConstraintName.ShouldBe($"ck_{Table}_status");

        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM {Table} WHERE external_account_id IN ('chan-platform', 'chan-status')")
            .ShouldBe(0, "a refused row is not written at all — the CHECK is not a default-filling one");

        // Both accepted platforms insert: `tiktok` is in the CHECK already, because 07 stores it here.
        SqlProbe.Execute(Database, InsertSql("youtube", "active", "chan-valid"));
        SqlProbe.Execute(Database, InsertSql("tiktok", "active", "chan-tiktok"));
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM {Table} WHERE external_account_id IN ('chan-valid', 'chan-tiktok')")
            .ShouldBe(2);

        // No foreign key to `users` (13 §5.4, the `email_messages` precedent): a row whose owner has no
        // `users` row inserts, so a deleted account leaves a connection behind instead of failing the purge.
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'f'")
            .ShouldBe(0);
        SqlProbe.Execute(
            Database,
            InsertSql("youtube", "active", "chan-orphan", "01920000-0000-7000-8000-0000000000ff"));
    }

    /// <summary>
    /// The one-owner rule is the partial unique index, not a handler convention (06 README D3): two live rows
    /// for one Google channel are refused by PostgreSQL, while the same insert succeeds once the earlier row is
    /// revoked, because <c>status &lt;&gt; 'revoked'</c> takes it out of the key.
    /// </summary>
    [Fact]
    public void AccountIndex_RefusesTwoLiveRows_AndAllowsTheDuplicateOnceOneIsRevoked()
    {
        SqlProbe.Execute(Database, InsertSql("youtube", "active", "chan-live"));

        var duplicate = Should.Throw<PostgresException>(
            () => SqlProbe.Execute(Database, InsertSql("youtube", "active", "chan-live")));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe($"ux_{Table}_account");

        // A second channel is a different key: the index is per (platform, external_account_id).
        SqlProbe.Execute(Database, InsertSql("youtube", "active", "chan-other"));

        // The raw UPDATE is the schema's view of a revoke: it leaves `revoked_at` and the tokens alone, because
        // nothing in the table ties them together — the aggregate's `Revoke` owns that, and this test only needs
        // the status the partial index reads.
        SqlProbe.Execute(
            Database,
            $"UPDATE {Table} SET status = 'revoked' WHERE external_account_id = 'chan-live'");

        // The revoked row left the index, so the channel can be connected again — by its former owner or by
        // anyone else — and the historical row stays for the audit/list evidence.
        SqlProbe.Execute(Database, InsertSql("youtube", "active", "chan-live"));

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE external_account_id = 'chan-live'")
            .ShouldBe(2);
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
    /// the inherited rows come out untouched and <c>channel_connections</c> joins them empty. The seed skips a
    /// table the previous schema does not have, so this run writes no connection row of its own — that row
    /// lands in the next migration's run.
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
            // One release behind is 06.1.2-T1's `_06_OAuthStates`, which does have the table of its own
            // dataset entry: the seed must really write data into N-1 for the comparison to mean anything.
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
                .ShouldBe("gone");

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldNotContain(Table, "the previous schema has no table to seed a connection into");
            seeded.ShouldContain("oauth_states", "the migration must be exercised against data, not an empty schema");
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
        // A connection row of the schema the test starts from, so the Down is exercised over data.
        SqlProbe.Execute(Database, InsertSql("youtube", "active", "chan-down"));

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
            foreach (var sibling in new[] { "oauth_states", "platform_quota_usage" })
            {
                SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{sibling}')::text, 'gone')")
                    .ShouldNotBe("gone", "the Down of this migration must not touch the sibling Publishing tables");
            }
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
    /// One connection as the connect path writes it: every <c>NOT NULL</c> column, with only the platform, the
    /// status and the account id under the caller's control, so no other column can make an insert fail first.
    /// </summary>
    private static string InsertSql(string platform, string status, string externalAccountId, string? owner = null) =>
        $"INSERT INTO {Table} (id, user_id, platform, external_account_id, display_name, status, access_token_enc, "
        + "refresh_token_enc, token_expires_at, scope, oauth_client_id, last_verified_at, connected_at) VALUES ("
        + $"'{Guid.CreateVersion7()}', '{owner ?? Owner}', '{platform}', '{externalAccountId}', 'Seed channel', "
        + $"'{status}', 'seed-access-token-enc', 'seed-refresh-token-enc', now() + interval '1 hour', '{Scope}', "
        + "'seed-client', now(), now())";

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
