using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The <c>_11_WorkerNodes</c> migration and its database-level rules (11.3.3-T1 plan §3/§9): the table
/// matches 00 §6 L311 column-for-column, the <c>name</c> is unique, the three <c>CHECK</c>s refuse a state
/// or capacity the domain would refuse, and the <c>Down</c> drops the table and leaves an empty one when the
/// migration is applied again.
/// </summary>
/// <remarks>
/// The shape is read from two sides on purpose, like <c>DataProtectionKeysMigrationTests</c>: the EF model
/// (what the next <c>dotnet ef migrations add</c> would diff against) and the live schema of the class
/// database (what <c>database update</c> actually applied).
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class WorkerNodesMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_CreatesWorkerNodesWithConstraints()
    {
        RenderNodeDatabase.Reset(Database);
        await using var context = Database.CreateAppDbContext();

        var entity = context.Model.FindEntityType(typeof(WorkerNode));
        entity.ShouldNotBeNull("the model must map the WorkerNode registry (11 README D13)");
        entity!.GetTableName().ShouldBe("worker_nodes");

        entity.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ShouldBe(ExpectedColumns, ignoreOrder: false);

        // 00 §6 L311 column-for-column, including the two the settings UI owns but P leaves null (F09).
        (await Database.QueryStringsAsync(
            "SELECT column_name || ':' || data_type FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'worker_nodes' ORDER BY column_name",
            Ct))
            .ShouldBe(ExpectedColumnTypes, ignoreOrder: false);

        (await Database.QueryStringsAsync(
            "SELECT column_name || ':' || is_nullable FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'worker_nodes' "
            + "AND column_name IN ('api_key_ciphertext', 'api_key_hint') ORDER BY column_name",
            Ct))
            .ShouldBe(["api_key_ciphertext:YES", "api_key_hint:YES"], ignoreOrder: false);

        // The unique name is the seeder's upsert key and the secret-manager variable's base (D13).
        (await Database.QueryStringsAsync(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'worker_nodes' AND indexname = 'ux_worker_nodes_name'",
            Ct))
            .ShouldHaveSingleItem().ShouldContain("UNIQUE INDEX");

        // The three value constraints mirror the domain: capacity 1..8, the closed state set, and a pin that
        // is absent exactly for a plain http:// URL (T39).
        (await Database.QueryStringsAsync(
            "SELECT conname FROM pg_constraint WHERE conrelid = 'worker_nodes'::regclass AND contype = 'c' "
            + "ORDER BY conname",
            Ct))
            .ShouldBe(["ck_worker_nodes_capacity", "ck_worker_nodes_state", "ck_worker_nodes_tls_sha256"]);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");
    }

    [Fact]
    public async Task UniqueName_RejectsDuplicate()
    {
        RenderNodeDatabase.Reset(Database);
        SqlProbe.Execute(Database, Insert("'alpha'", capacity: 2, state: "'active'"));

        SqlProbe.SqlState(Database, Insert("'alpha'", capacity: 3, state: "'active'"))
            .ShouldBe("23505", "worker_nodes.name is the natural key: two rows with one name would split the registry");
    }

    [Fact]
    public async Task CheckRejectsUnknownState()
    {
        RenderNodeDatabase.Reset(Database);
        SqlProbe.SqlState(Database, Insert("'alpha'", capacity: 2, state: "'paused'"))
            .ShouldBe("23514", "the state's closed set is active/draining/down (D13)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task CheckRejectsCapacityOutOfRange(int capacity)
    {
        RenderNodeDatabase.Reset(Database);
        var state = $"'{Guid.CreateVersion7():N}'";

        SqlProbe.SqlState(Database, Insert(state, capacity, "'active'"))
            .ShouldBe("23514", "capacity is a slot count in 1..8 (05 README L138)");
    }

    [Fact]
    public async Task DownMigration_DropsTable()
    {
        RenderNodeDatabase.Reset(Database);
        await using var context = Database.CreateAppDbContext();
        SqlProbe.Execute(Database, Insert("'alpha'", capacity: 2, state: "'active'"));

        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldContain("11_WorkerNodes", customMessage: "this test owns the last migration");
        migrations.Count.ShouldBeGreaterThan(1, "there must be a previous migration to revert to");

        try
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(migrations[^2], Ct);

            (await Database.QueryStringsAsync("SELECT to_regclass('public.worker_nodes')::text", Ct))
                .ShouldBe([string.Empty], ignoreOrder: false, customMessage: "the Down drops the table");
        }
        finally
        {
            // Always come back to the latest migration, so a failed assertion does not leave the class
            // database on the previous schema for its sibling tests.
            await context.Database.MigrateAsync(CancellationToken.None);
        }

        (await Database.QueryStringsAsync("SELECT to_regclass('public.worker_nodes')::text", Ct))
            .ShouldHaveSingleItem().ShouldBe("worker_nodes", "the Up re-creates the table");
        SqlProbe.Count(Database, "SELECT count(*) FROM worker_nodes")
            .ShouldBe(0, "the re-created table is empty: the Down dropped the seeded row with it");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    private static string Insert(string name, int capacity, string state) =>
        "INSERT INTO worker_nodes (id, name, base_url, capacity, state, tls_cert_sha256, created_at, updated_at) "
        + "VALUES (gen_random_uuid(), " + name + ", 'https://node.example/', "
        + capacity.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + state
        + ", repeat('AB', 32), now(), now())";

    private static readonly string[] ExpectedColumns =
    [
        "api_key_ciphertext",
        "api_key_hint",
        "base_url",
        "capacity",
        "created_at",
        "gpu_model",
        "id",
        "last_heartbeat_at",
        "name",
        "state",
        "tls_cert_sha256",
        "updated_at",
    ];

    private static readonly string[] ExpectedColumnTypes =
    [
        "api_key_ciphertext:bytea",
        "api_key_hint:character varying",
        "base_url:character varying",
        "capacity:integer",
        "created_at:timestamp with time zone",
        "gpu_model:character varying",
        "id:uuid",
        "last_heartbeat_at:timestamp with time zone",
        "name:character varying",
        "state:text",
        "tls_cert_sha256:character varying",
        "updated_at:timestamp with time zone",
    ];
}
