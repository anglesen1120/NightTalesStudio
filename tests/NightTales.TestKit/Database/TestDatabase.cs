using System.Diagnostics;
using Npgsql;
using NightTales.Application.Abstractions;
using NightTales.Infrastructure.Persistence;
using Xunit;

namespace NightTales.TestKit.Database;

/// <summary>
/// One PostgreSQL database for one test class (09 README D13): a <c>CREATE DATABASE … TEMPLATE</c>
/// copy of the migrated template, dropped with <c>FORCE</c> when the class is over. xUnit creates it
/// as the class fixture declared by <see cref="IntegrationTest"/>; a test may also create one by hand
/// to prove the copy/drop behaviour.
/// </summary>
/// <remarks>
/// The name is generated per instance, so parallel test classes never share a database and one run
/// cannot read another class's rows. <see cref="ConnectionString"/> is <c>NTS_TEST_PG</c> with
/// <c>Database</c> swapped, so credentials always come from the one environment variable; it is built
/// once per instance (the getter is called on every context creation). The name carries the reserved
/// <see cref="PostgresFixture.ManagedDatabasePrefix"/>, which the fixture requires before any
/// destructive DDL.
/// </remarks>
public sealed class TestDatabase : IAsyncLifetime, IAsyncDisposable
{
    // xUnit's fixture factory requires exactly one public constructor, so the fixture is always the
    // shared one; tests that need a second database create another instance of this class.
    private readonly PostgresFixture _fixture = PostgresFixture.Shared;
    private bool _created;

    /// <summary>
    /// Names this database and resolves its connection string once; the server work is
    /// <see cref="InitializeAsync"/>.
    /// </summary>
    public TestDatabase()
    {
        Name = PostgresFixture.RequireManagedDatabaseName($"nts_t_{Guid.CreateVersion7():N}");
        ConnectionString = _fixture.ConnectionStringFor(Name);
    }

    /// <summary>The fixture that owns the template this database is copied from.</summary>
    public PostgresFixture Fixture => _fixture;

    /// <summary>This database's name on the test server, unique per instance.</summary>
    public string Name { get; }

    /// <summary>The pooled connection string of this database, computed once (PERF 09.2.1-T2).</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// How long the <c>CREATE DATABASE … TEMPLATE</c> copy of <see cref="InitializeAsync"/> took, once
    /// it has run — the template build itself is excluded because only the first class of a run pays
    /// it. The class fixture creates the database before the test class exists, so this is the number
    /// a test reports for the "one class database in under a second" acceptance check.
    /// </summary>
    public TimeSpan? CreationDuration { get; private set; }

    /// <summary>Creates the database from the template (once; the template itself is built once per run).</summary>
    public async ValueTask InitializeAsync()
    {
        // The template is built at most once per run and the first class pays for it: it is outside
        // the measurement, which must be the copy this class waits for.
        await _fixture.EnsureReadyAsync();

        var startedAt = Stopwatch.GetTimestamp();
        await _fixture.CreateDatabaseAsync(Name, _fixture.TemplateDatabase);

        _created = true;
        CreationDuration = Stopwatch.GetElapsedTime(startedAt);
    }

    /// <summary>Drops the database with <c>FORCE</c>, which terminates a test's leaked connections.</summary>
    /// <remarks>
    /// This database's Npgsql pool is cleared first: <c>DROP DATABASE … WITH (FORCE)</c> has to
    /// terminate every backend, and terminating an *idle pooled* connection costs hundreds of
    /// milliseconds, while a connection that is already closed costs nothing (PERF 09.2.1-T2). The
    /// clear is scoped to exactly this connection string, so a parallel class's pool is untouched.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (!_created)
        {
            return;
        }

        _created = false;

        using (var pooled = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        await _fixture.DropDatabaseAsync(Name);
    }

    /// <summary>A write context over this database; <paramref name="currentUser"/> defaults to no actor.</summary>
    public AppDbContext CreateAppDbContext(ICurrentUser? currentUser = null) =>
        TestDbContextFactory.CreateAppContext(ConnectionString, currentUser);

    /// <summary>A read context over this database.</summary>
    public ReadDbContext CreateReadDbContext(ICurrentUser? currentUser = null) =>
        TestDbContextFactory.CreateReadContext(ConnectionString, currentUser);

    /// <summary>A builder that inserts real <c>users</c> rows into this database.</summary>
    public Builders.UserBuilder CreateUserBuilder(Auth.TestCurrentUser? currentUser = null) =>
        new(this, currentUser ?? new Auth.TestCurrentUser());

    /// <summary>Runs one statement against this database (helpful for catalog assertions).</summary>
    public Task ExecuteAsync(string sql, CancellationToken ct = default) => _fixture.ExecuteAsync(Name, sql, ct);

    /// <summary>Reads the first column of every row as a string from this database.</summary>
    public Task<IReadOnlyList<string>> QueryStringsAsync(string sql, CancellationToken ct = default) =>
        _fixture.QueryStringsAsync(Name, sql, ct);
}
