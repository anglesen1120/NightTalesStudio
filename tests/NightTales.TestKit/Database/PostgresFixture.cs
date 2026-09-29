using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace NightTales.TestKit.Database;

/// <summary>
/// Gives every integration test class its own migrated database on the real PostgreSQL 17 server
/// named by <c>NTS_TEST_PG</c> (09 README D13, 09 W3 CR-35). No container is started and no
/// container library is referenced: the fixture connects to the server the developer or the CI job
/// already runs.
/// </summary>
/// <remarks>
/// <para>
/// Once per run the fixture rebuilds the template database <c>nts_template</c> (drop with
/// <c>FORCE</c>, create, apply the compiled migrations); every <see cref="TestDatabase"/> is then a
/// <c>CREATE DATABASE … TEMPLATE nts_template</c> copy, which costs well under a second and does not
/// grow with the number of test classes.
/// </para>
/// <para>
/// The fixture is shared per test assembly through <see cref="Shared"/>. It is deliberately *not*
/// registered with <c>[assembly: AssemblyFixture]</c>: xUnit v3 creates every registered assembly
/// fixture for a run that has at least one test, whether or not a test uses it, and the unit job runs
/// without <c>NTS_TEST_PG</c> — so a registered fixture would fail the unit job. Reaching the real
/// work through <see cref="EnsureReadyAsync"/> keeps it lazy and still once per run.
/// </para>
/// <para>
/// Nothing is ever skipped: a missing/blank <c>NTS_TEST_PG</c>, an unparsable value or a server that
/// is not major 17 fails with an <see cref="InvalidOperationException"/> that names the variable.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime, IAsyncDisposable
{
    /// <summary>The environment variable holding the test server connection string.</summary>
    public const string ConnectionStringVariable = "NTS_TEST_PG";

    /// <summary>The template database every per-class database is copied from, rebuilt once per run.</summary>
    public const string TemplateDatabaseName = "nts_template";

    /// <summary>The only PostgreSQL major the harness supports (09 README D8, owner decision O3).</summary>
    public const int RequiredServerMajor = 17;

    private static readonly Lazy<PostgresFixture> SharedInstance = new(static () => new PostgresFixture());

    private readonly Lazy<string> _serverConnectionString;
    private readonly Lazy<Task> _ready;

    /// <summary>Creates the fixture over the template database <see cref="TemplateDatabaseName"/>.</summary>
    public PostgresFixture()
        : this(TemplateDatabaseName)
    {
    }

    /// <summary>
    /// Creates the fixture over a template database with a custom name. Only the staleness test uses
    /// a second name: rebuilding <c>nts_template</c> while parallel test classes copy from it would
    /// be a race.
    /// </summary>
    public PostgresFixture(string templateDatabaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateDatabaseName);
        TemplateDatabase = templateDatabaseName;
        _serverConnectionString = new Lazy<string>(
            static () => RequireServerConnectionString(Environment.GetEnvironmentVariable(ConnectionStringVariable)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _ready = new Lazy<Task>(InitializeOnceAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The fixture shared by every test class of this test assembly.</summary>
    public static PostgresFixture Shared => SharedInstance.Value;

    /// <summary>The template database this fixture rebuilds and copies per-class databases from.</summary>
    public string TemplateDatabase { get; }

    /// <summary>The unmodified <c>NTS_TEST_PG</c> value; reading it performs the blank-value check.</summary>
    public string ServerConnectionString => _serverConnectionString.Value;

    /// <summary>
    /// Reads the environment variable, checks the server major and rebuilds the template database —
    /// exactly once per fixture instance. Every <see cref="TestDatabase"/> awaits this first.
    /// </summary>
    public Task EnsureReadyAsync() => _ready.Value;

    /// <inheritdoc />
    public ValueTask InitializeAsync() => new(EnsureReadyAsync());

    /// <summary>Nothing to release: the template database stays for the next run to rebuild.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Validates the raw <c>NTS_TEST_PG</c> value (blank check + connection-string syntax) without
    /// connecting, so the guard is unit-testable. A blank value is the named failure of 09 README
    /// D13; it is never a skip.
    /// </summary>
    public static string RequireServerConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is required for integration tests: set it to the PostgreSQL {RequiredServerMajor} " +
                "connection string (see docs/dev-setup.md).");
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            // A parser message can echo a malformed keyword/value fragment (NTS-SEC-SECRETS-001),
            // so only a constant message reaches the test output.
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} is not a valid connection string (see docs/dev-setup.md).");
        }

        return value;
    }

    /// <summary>
    /// The major-17 guard, factored out of the connection so the negative cases need no server.
    /// <paramref name="serverVersionNum"/> is PostgreSQL's <c>server_version_num</c> (for example
    /// <c>170006</c>, whose major is <c>170006 / 10000 = 17</c>) and <paramref name="serverVersion"/>
    /// its human-readable form (<c>17.6</c>); both are reported in the message.
    /// </summary>
    public static void EnsureServerMajor17(string serverVersionNum, string serverVersion)
    {
        if (!int.TryParse(serverVersionNum, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            || numeric / 10000 != RequiredServerMajor)
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} must point at PostgreSQL {RequiredServerMajor} " +
                $"(found {serverVersion}, server_version_num {serverVersionNum}); see docs/dev-setup.md.");
        }
    }

    /// <summary>
    /// Renders a connection string with the password replaced, for messages and logs. It never throws:
    /// an unparsable value (whose parser message can echo a credential fragment) and a missing value
    /// both become a placeholder.
    /// </summary>
    public static string MaskPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "<no connection string>";
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString) { Password = "***" }.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "<unparsable connection string>";
        }
    }

    /// <summary>
    /// The pooled connection string of one database on the test server: <c>NTS_TEST_PG</c> with
    /// <c>Database</c> swapped, so the credentials always come from the same environment variable.
    /// </summary>
    public string ConnectionStringFor(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = databaseName }.ConnectionString;
    }

    /// <summary>Runs one statement against the server database of <c>NTS_TEST_PG</c>.</summary>
    public async Task ExecuteOnServerAsync(string sql, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ServerConnectionString, ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Runs one statement against <paramref name="databaseName"/> on the test server.</summary>
    public async Task ExecuteAsync(string databaseName, string sql, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = await OpenAsync(HelperConnectionString(databaseName), ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Rebuilds <paramref name="databaseName"/> from scratch: drop (with <c>FORCE</c>, so a leaked
    /// connection cannot block the run), create, then migrate the compiled migrations of
    /// <c>AppDbContext</c>. <see cref="EnsureReadyAsync"/> calls this once per run for
    /// <see cref="TemplateDatabase"/>; the staleness test calls it on an isolated name.
    /// </summary>
    public async Task RebuildTemplateAsync(string databaseName, CancellationToken ct = default)
    {
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE)", ct);
        await ExecuteOnServerAsync($"CREATE DATABASE {QuoteIdentifier(databaseName)}", ct);

        // A connection of its own, never pooled: an idle pooled backend on the template would make
        // every later `CREATE DATABASE … TEMPLATE` fail with "source database is being accessed".
        await using var context = TestDbContextFactory.CreateAppContext(HelperConnectionString(databaseName));

        if (context.Database.GetMigrations().Any())
        {
            await context.Database.MigrateAsync(ct);
        }
        else
        {
            // 09.2.2-T2 owns `InitialCore`; until it lands this context compiles zero migrations, so
            // `Migrate` is a no-op and the template would carry no schema at all. Build the model
            // directly for that window — the branch is dead from the first migration on.
            await context.Database.EnsureCreatedAsync(ct);
        }
    }

    /// <summary>True when the server catalog holds <paramref name="databaseName"/>.</summary>
    public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ServerConnectionString, ct);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @database_name)",
            connection);
        command.Parameters.AddWithValue("database_name", databaseName);

        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// The <c>pg_database</c> oid of <paramref name="databaseName"/>, or <c>null</c> when it does not
    /// exist. PostgreSQL assigns a new oid on every <c>CREATE DATABASE</c>, so comparing it before and
    /// after a rebuild proves the database was dropped and re-created, not merely re-migrated.
    /// </summary>
    public async Task<uint?> DatabaseOidAsync(string databaseName, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ServerConnectionString, ct);
        await using var command = new NpgsqlCommand("SELECT oid FROM pg_database WHERE datname = @database_name", connection);
        command.Parameters.AddWithValue("database_name", databaseName);

        return await command.ExecuteScalarAsync(ct) is uint oid ? oid : null;
    }

    /// <summary>The migrations applied to <paramref name="databaseName"/>, ordinal-sorted.</summary>
    public async Task<IReadOnlyList<string>> AppliedMigrationsAsync(string databaseName, CancellationToken ct = default)
    {
        await using var context = TestDbContextFactory.CreateAppContext(HelperConnectionString(databaseName));
        var applied = await context.Database.GetAppliedMigrationsAsync(ct);

        return [.. applied.Order(StringComparer.Ordinal)];
    }

    /// <summary>The compiled migrations <paramref name="databaseName"/> does not have yet.</summary>
    public async Task<IReadOnlyList<string>> PendingMigrationsAsync(string databaseName, CancellationToken ct = default)
    {
        await using var context = TestDbContextFactory.CreateAppContext(HelperConnectionString(databaseName));
        var pending = await context.Database.GetPendingMigrationsAsync(ct);

        return [.. pending.Order(StringComparer.Ordinal)];
    }

    /// <summary>The <c>public</c> tables of <paramref name="databaseName"/>, ordinal-sorted.</summary>
    public async Task<IReadOnlyList<string>> TableNamesAsync(string databaseName, CancellationToken ct = default)
    {
        var names = await QueryStringsAsync(
            databaseName,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'",
            ct);

        return [.. names.Order(StringComparer.Ordinal)];
    }

    /// <summary>Reads the first column of every row as a string.</summary>
    public async Task<IReadOnlyList<string>> QueryStringsAsync(
        string databaseName,
        string sql,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var connection = await OpenAsync(HelperConnectionString(databaseName), ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var values = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Quotes a database name for DDL. DDL cannot take parameters, so the name is restricted to
    /// unquoted-safe characters instead: every name in the harness is generated or a constant.
    /// </summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Length is 0 or > 63
            || !name.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new ArgumentException($"'{name}' is not a valid test database identifier.", nameof(name));
        }

        return $"\"{name}\"";
    }

    /// <summary>Connection string for a fixture-side helper connection (never pooled).</summary>
    private string HelperConnectionString(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (NpgsqlException exception)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"{ConnectionStringVariable}: the test server is not reachable ({MaskPassword(connectionString)}); " +
                "start it as described in docs/dev-setup.md.",
                exception);
        }

        return connection;
    }

    private async Task InitializeOnceAsync()
    {
        var connectionString = ServerConnectionString;

        await using (var connection = await OpenAsync(connectionString, CancellationToken.None))
        {
            await using var command = new NpgsqlCommand(
                "SELECT current_setting('server_version_num'), current_setting('server_version')",
                connection);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

            if (!await reader.ReadAsync(CancellationToken.None))
            {
                throw new InvalidOperationException(
                    $"{ConnectionStringVariable}: the test server did not report its version ({MaskPassword(connectionString)}).");
            }

            EnsureServerMajor17(reader.GetString(0), reader.GetString(1));
        }

        await RebuildTemplateAsync(TemplateDatabase, CancellationToken.None);
    }
}
