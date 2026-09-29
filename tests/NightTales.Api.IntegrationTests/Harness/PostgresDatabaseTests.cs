using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// The database behaviour of the harness itself (09 README D13): one isolated, migrated database per
/// test class, a template rebuilt once per run, and a drop when the class is over.
/// </summary>
/// <remarks>
/// The template itself is never queried while the run is live: any connection to
/// <c>nts_template</c> would make a parallel class's <c>CREATE DATABASE … TEMPLATE</c> fail with
/// "source database is being accessed by other users". Every statement therefore runs against the
/// class database — a byte copy of the template — or against the server catalog.
/// </remarks>
public sealed class PostgresDatabaseTests : IntegrationTest
{
    private readonly ITestOutputHelper _output;

    public PostgresDatabaseTests(TestDatabase database, ITestOutputHelper output)
        : base(database) => _output = output;

    private PostgresFixture Fixture => Database.Fixture;

    [Fact]
    public async Task PostgresFixture_CreatesIsolatedDatabasePerClass()
    {
        var ct = TestContextToken.Value;

        Database.CreationDuration.ShouldNotBeNull();
        _output.WriteLine(
            $"class database {Database.Name} created in {Database.CreationDuration.Value.TotalMilliseconds:F0} ms");
        Database.CreationDuration.Value.ShouldBeLessThan(
            TimeSpan.FromSeconds(1),
            "one class database is a CREATE DATABASE … TEMPLATE copy and must stay well under a second");

        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        var other = new TestDatabase();
        try
        {
            var startedAt = Stopwatch.GetTimestamp();
            await other.InitializeAsync();
            var duration = Stopwatch.GetElapsedTime(startedAt);
            _output.WriteLine($"second database {other.Name} created in {duration.TotalMilliseconds:F0} ms");
            duration.ShouldBeLessThan(TimeSpan.FromSeconds(1));

            other.Name.ShouldNotBe(Database.Name);

            // SEC 09.2.1-T2: the raw connection strings embed the password, so they are never
            // compared or printed directly — a failed assertion would leak it into the test output.
            PostgresFixture.MaskPassword(other.ConnectionString)
                .ShouldNotBe(PostgresFixture.MaskPassword(Database.ConnectionString));
            new NpgsqlConnectionStringBuilder(other.ConnectionString).Database.ShouldBe(other.Name);
            new NpgsqlConnectionStringBuilder(Database.ConnectionString).Database.ShouldBe(Database.Name);

            await using (var here = Database.CreateReadDbContext())
            {
                (await here.Query<AppUser>().AnyAsync(row => row.Id == user.Id, ct))
                    .ShouldBeTrue("the row was written into this class's database");
            }

            await using (var there = other.CreateReadDbContext())
            {
                (await there.Query<AppUser>().AnyAsync(row => row.Id == user.Id, ct))
                    .ShouldBeFalse("another class's database never sees this class's rows");
            }
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostgresFixture_TemplateIsMigratedOnce()
    {
        var ct = TestContextToken.Value;
        var fixture = Fixture;

        // The template is a byte copy in every class database, so the copy is what can be asserted
        // without racing the parallel copies (see the class remarks).
        (await fixture.DatabaseExistsAsync(fixture.TemplateDatabase, ct))
            .ShouldBeTrue("the run builds nts_template before the first class database");

        await using var context = Database.CreateAppDbContext();
        var compiled = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();

        var applied = await fixture.AppliedMigrationsAsync(Database.Name, ct);
        applied.ShouldBe(compiled, "a class database carries exactly the migrations this run compiled");

        (await fixture.PendingMigrationsAsync(Database.Name, ct)).ShouldBeEmpty();
        (await fixture.TableNamesAsync(Database.Name, ct))
            .ShouldContain("users", "the template is built from the compiled model, not left empty");

        var tablesBefore = await fixture.TableNamesAsync(Database.Name, ct);
        await fixture.EnsureReadyAsync();
        (await fixture.TableNamesAsync(Database.Name, ct))
            .ShouldBe(tablesBefore, "EnsureReadyAsync is idempotent: the template is rebuilt once per run");
    }

    [Fact]
    public async Task PostgresFixture_RebuildsStaleTemplate_OnRunStart()
    {
        var ct = TestContextToken.Value;

        // A second fixture points at an isolated template name: rebuilding the shared nts_template
        // during the run would race the parallel classes copying from it. The rebuild is the same
        // code path the run start uses (RebuildTemplateAsync), only the name differs.
        const string StaleName = "nts_stale_probe";
        var stale = new PostgresFixture(StaleName);

        try
        {
            // Simulate the template an earlier run left behind: a database whose schema is not the
            // compiled one (an "older migration set"). Only the guarded DDL helpers are public now
            // (SEC 09.2.1-T2), which is all this test needs.
            await stale.DropDatabaseAsync(StaleName, ct);
            await stale.CreateDatabaseAsync(StaleName, templateDatabaseName: null, ct);
            await stale.ExecuteAsync(StaleName, "CREATE TABLE stale_marker (id integer)", ct);

            (await stale.TableNamesAsync(StaleName, ct)).ShouldContain("stale_marker");
            var staleOid = await stale.DatabaseOidAsync(StaleName, ct);

            await stale.InitializeAsync();

            (await stale.TableNamesAsync(StaleName, ct)).ShouldNotContain("stale_marker");
            (await stale.TableNamesAsync(StaleName, ct)).ShouldContain("users");
            (await stale.PendingMigrationsAsync(StaleName, ct)).ShouldBeEmpty();
            (await stale.DatabaseOidAsync(StaleName, ct))
                .ShouldNotBe(staleOid, "a rebuild drops and recreates the database, so PostgreSQL assigns a new oid");
        }
        finally
        {
            // Cleanup must run even when the test was cancelled, so it does not take the test token.
            await stale.DropDatabaseAsync(StaleName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task PostgresFixture_DropsClassDatabase_AtEndOfClass()
    {
        var ct = TestContextToken.Value;
        var fixture = Fixture;
        var database = new TestDatabase();

        await database.InitializeAsync();
        (await fixture.DatabaseExistsAsync(database.Name, ct)).ShouldBeTrue();
        await database.ExecuteAsync("CREATE TABLE dropped_marker (id integer)", ct);

        await database.DisposeAsync();

        (await fixture.DatabaseExistsAsync(database.Name, ct))
            .ShouldBeFalse("the class fixture drops its database with FORCE when the class is over");

        // Disposing twice is a no-op, so a failure between create and drop never throws on cleanup.
        await database.DisposeAsync();
    }



    [Fact]
    public async Task PostgresFixture_ForceDrop_TerminatesLeakedConnections()
    {
        // QA 09.2.1-T2: a test that leaks an open connection must not block the class-database drop
        // (DROP DATABASE … WITH (FORCE) terminates the backend).
        var ct = TestContextToken.Value;
        var fixture = Fixture;
        var database = new TestDatabase();
        NpgsqlConnection? leaked = null;

        try
        {
            await database.InitializeAsync();

            leaked = new NpgsqlConnection(database.ConnectionString);
            await leaked.OpenAsync(ct);
            await using var keepAlive = new NpgsqlCommand("SELECT 1", leaked);
            (await keepAlive.ExecuteScalarAsync(ct)).ShouldBe(1);

            // The connection is still open while the fixture drops the database.
            await database.DisposeAsync();

            (await fixture.DatabaseExistsAsync(database.Name, ct))
                .ShouldBeFalse("WITH (FORCE) must drop the database despite a leaked connection");

            // The leaked backend was terminated: the next use of the connection fails.
            await Should.ThrowAsync<Exception>(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT 1", leaked);
                await command.ExecuteScalarAsync(ct);
            });
        }
        finally
        {
            if (leaked is not null)
            {
                await leaked.DisposeAsync();
            }

            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostgresFixture_ParallelCreates_ProduceIsolatedDatabases()
    {
        // QA 09.2.1-T2: xunit.v3 keeps class parallelism on; two databases created concurrently
        // from the shared template must both succeed and stay distinct.
        var first = new TestDatabase();
        var second = new TestDatabase();

        try
        {
            await Task.WhenAll(
                first.InitializeAsync().AsTask(),
                second.InitializeAsync().AsTask());

            first.Name.ShouldNotBe(second.Name);
            (await Fixture.DatabaseExistsAsync(first.Name, TestContextToken.Value)).ShouldBeTrue();
            (await Fixture.DatabaseExistsAsync(second.Name, TestContextToken.Value)).ShouldBeTrue();

            // A row in one is invisible in the other: the isolation a parallel pair relies on.
            var user = await first.CreateUserBuilder().CreateAsync(ct: TestContextToken.Value);
            (await second.QueryStringsAsync(
                    $"SELECT id::text FROM users WHERE id = '{user.Id}'",
                    TestContextToken.Value))
                .ShouldBeEmpty();
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }
}
