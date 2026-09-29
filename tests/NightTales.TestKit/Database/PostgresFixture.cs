using System.Globalization;
using System.Net;
using System.Text;
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
/// <para>
/// The <c>Database</c> entry of <c>NTS_TEST_PG</c> is only where the fixture runs its own DDL
/// (<c>CREATE DATABASE</c>, <c>DROP DATABASE</c> and the catalog queries); no test ever writes there,
/// because every test class gets its own fresh <c>nts_…</c> database. Any database the role can
/// connect to works, so the value carries no state: <c>tools/dev/db-init.sh</c> makes the <c>.env</c>
/// database (<c>nighttales</c> on this Mac) connectable, and the docs use that value.
/// </para>
/// <para>
/// Because the fixture drops and recreates databases, two boundaries are enforced before any DDL:
/// every name it creates or drops must carry the reserved prefix <see cref="ManagedDatabasePrefix"/>
/// (see <see cref="RequireManagedDatabaseName"/>), and the server of <c>NTS_TEST_PG</c> must be a
/// loopback host unless <see cref="AllowRemoteDdlVariable"/><c>=1</c> is set explicitly (see
/// <see cref="EnsureDdlTargetIsLoopback"/>). The harness-owned Worker hosts install Hangfire's schema
/// themselves, so they pass their connection string through the same two boundaries first
/// (<see cref="RequireManagedWorkerConnectionString"/>).
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime, IAsyncDisposable
{
    /// <summary>The environment variable holding the test server connection string.</summary>
    public const string ConnectionStringVariable = "NTS_TEST_PG";

    /// <summary>
    /// The variable that must hold <c>1</c> to let the fixture run its destructive DDL against a
    /// non-loopback server (<see cref="EnsureDdlTargetIsLoopback"/>). It must never be set for a
    /// shared or production cluster.
    /// </summary>
    public const string AllowRemoteDdlVariable = "NTS_TEST_PG_ALLOW_REMOTE_DDL";

    /// <summary>
    /// Every database the fixture creates or drops must carry this reserved prefix, so a stray value
    /// or a typo can never hand the harness a shared database (<see cref="RequireManagedDatabaseName"/>).
    /// </summary>
    public const string ManagedDatabasePrefix = "nts_";

    /// <summary>The destructive DDL statements the harness runs against the test server.</summary>
    public enum DdlStatement
    {
        /// <summary><c>CREATE DATABASE</c>, used by the template rebuild and by every class database.</summary>
        CreateDatabase,

        /// <summary><c>DROP DATABASE … WITH (FORCE)</c>, used before a rebuild and for a class database.</summary>
        DropDatabase,
    }

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
        RequireManagedDatabaseName(templateDatabaseName);
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
    /// Validates the raw <c>NTS_TEST_PG</c> value (blank check + connection-string syntax + the
    /// loopback DDL boundary) without connecting, so the guard is unit-testable. A blank value is the
    /// named failure of 09 README D13; it is never a skip.
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

        EnsureDdlTargetIsLoopback(value, IsRemoteDdlAllowed());

        return value;
    }

    /// <summary>True when <see cref="AllowRemoteDdlVariable"/> explicitly allows a remote DDL target.</summary>
    public static bool IsRemoteDdlAllowed() =>
        Environment.GetEnvironmentVariable(AllowRemoteDdlVariable) == "1";

    /// <summary>
    /// The loopback boundary of the destructive DDL: the fixture drops and recreates databases on the
    /// server of <c>NTS_TEST_PG</c>, so a target that is not this machine is refused unless
    /// <see cref="AllowRemoteDdlVariable"/> is <c>1</c>. The check is a pure function so the refusals
    /// need no server. A host that is unspecified is accepted: Npgsql resolves it locally (localhost
    /// or a Unix socket) by definition, so it can never be a remote cluster.
    /// </summary>
    /// <param name="connectionString">The raw <c>NTS_TEST_PG</c> value; must already parse.</param>
    /// <param name="allowRemote">Whether the owner explicitly allowed a remote DDL target.</param>
    public static void EnsureDdlTargetIsLoopback(string connectionString, bool allowRemote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Host may hold a comma-separated list (Npgsql multi-host); every entry must be loopback.
        foreach (var host in Hosts(new NpgsqlConnectionStringBuilder(connectionString).Host))
        {
            if (IsLoopbackHost(host))
            {
                continue;
            }

            if (allowRemote)
            {
                return;
            }

            throw new InvalidOperationException(
                $"{ConnectionStringVariable} points at '{host}', which is not a loopback host. The test fixture drops " +
                "and creates databases on that server, so it refuses a remote target unless " +
                $"{AllowRemoteDdlVariable}=1 is set explicitly. Never set that override for a shared or production " +
                "cluster: point it at a disposable test server only (see docs/dev-setup.md).");
        }
    }

    /// <summary>
    /// The destructive-DDL guard for the connection string a harness-owned host boots against
    /// (<see cref="NightTales.TestKit.Threading.WorkerHost"/>,
    /// <see cref="NightTales.TestKit.Health.WorkerHealthProbe"/>): those hosts run the Worker graph with
    /// <c>PrepareSchemaIfNecessary</c>, so their target must be this machine and carry the reserved
    /// <see cref="ManagedDatabasePrefix"/> prefix, exactly like the databases the fixture itself creates
    /// and drops. A blank value passes through, so the caller's own fail-fast error still wins.
    /// </summary>
    /// <param name="connectionString">The connection string handed to a harness-owned host; may be blank.</param>
    public static void RequireManagedWorkerConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // A parser message can echo a malformed keyword/value fragment (NTS-SEC-SECRETS-001),
            // so only a constant message reaches the test output.
            throw new InvalidOperationException(
                "The connection string handed to a harness-owned host is not a valid connection string "
                + "(see docs/dev-setup.md).");
        }

        EnsureDdlTargetIsLoopback(connectionString, IsRemoteDdlAllowed());

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException(
                "The connection string handed to a harness-owned host names no database; such a host installs Hangfire's "
                + $"schema, so it only accepts a database whose name starts with the reserved prefix '{ManagedDatabasePrefix}'.");
        }

        RequireManagedDatabaseName(database);
    }

    /// <summary>
    /// The destructive-DDL name boundary: only names carrying the reserved prefix
    /// <see cref="ManagedDatabasePrefix"/> may be created or dropped, so a stray name can never reach
    /// a shared database.
    /// </summary>
    /// <returns><paramref name="name"/>, so the call can wrap an assignment.</returns>
    public static string RequireManagedDatabaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!name.StartsWith(ManagedDatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing destructive DDL on database '{name}': the test harness only creates and drops databases whose " +
                $"name starts with the reserved prefix '{ManagedDatabasePrefix}'. Rename the database, or point " +
                $"{ConnectionStringVariable} at a dedicated test server (see docs/dev-setup.md).");
        }

        return name;
    }

    /// <summary>
    /// Maps a failed DDL statement to the named error of the failure table, or <c>null</c> when the
    /// SQLSTATE is not the privilege failure (those keep their original <see cref="PostgresException"/>).
    /// A pure function, so the mapping is unit-tested without a database.
    /// </summary>
    /// <param name="sqlState">The PostgreSQL SQLSTATE of the failure.</param>
    /// <param name="statement">The DDL statement that failed.</param>
    public static string? DescribeDdlFailure(string? sqlState, DdlStatement statement)
    {
        if (sqlState != PostgresErrorCodes.InsufficientPrivilege)
        {
            return null;
        }

        var kind = statement switch
        {
            DdlStatement.CreateDatabase => "CREATE DATABASE",
            DdlStatement.DropDatabase => "DROP DATABASE",
            _ => throw new ArgumentOutOfRangeException(nameof(statement), statement, "Unknown DDL statement kind."),
        };

        return $"{ConnectionStringVariable}: the test role may not run {kind} on the test server " +
            $"(SQLSTATE {sqlState}). The harness drops and rebuilds its template and gives every test class its own " +
            "database, so the role must own those databases and hold CREATEDB — run tools/dev/db-init.sh (09.1.1-T2) " +
            "and retry (see docs/dev-setup.md).";
    }

    /// <summary>
    /// Creates a database on the test server, with <c>TEMPLATE</c> when a template is given, mapping
    /// a privilege failure to the named error of <see cref="DescribeDdlFailure"/>.
    /// </summary>
    public async Task CreateDatabaseAsync(
        string databaseName,
        string? templateDatabaseName = null,
        CancellationToken ct = default)
    {
        var sql = new StringBuilder($"CREATE DATABASE {QuoteIdentifier(databaseName)}");
        if (templateDatabaseName is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" TEMPLATE {QuoteIdentifier(templateDatabaseName)}");
        }

        await ExecuteDdlAsync(DdlStatement.CreateDatabase, sql.ToString(), ct);
    }

    /// <summary>
    /// Drops a database with <c>FORCE</c>, mapping a privilege failure to the named error of
    /// <see cref="DescribeDdlFailure"/>.
    /// </summary>
    public Task DropDatabaseAsync(string databaseName, CancellationToken ct = default) =>
        ExecuteDdlAsync(
            DdlStatement.DropDatabase,
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE)",
            ct);

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
    /// The pooled connection string of one managed database on the test server: <c>NTS_TEST_PG</c>
    /// with <c>Database</c> swapped, so the credentials always come from the same environment
    /// variable. Only <see cref="ManagedDatabasePrefix"/> names are accepted (SEC 09.2.1-T2), so a
    /// connection string can never point the harness at a database it does not own.
    /// </summary>
    public string ConnectionStringFor(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        RequireManagedDatabaseName(databaseName);

        return new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = databaseName }.ConnectionString;
    }

    /// <summary>
    /// The raw server-level seam: one statement against the <c>NTS_TEST_PG</c> maintenance database.
    /// It is deliberately **private** (SEC 09.2.1-T2 hardening): arbitrary SQL there could drop any
    /// database on the server past the reserved-prefix rule, so the only public DDL paths are
    /// <see cref="CreateDatabaseAsync"/> and <see cref="DropDatabaseAsync"/>, which enforce the name
    /// and map the privilege failure. Every per-database helper validates the managed prefix too
    /// (<see cref="HelperConnectionString"/>), so no public member can run SQL anywhere else.
    /// </summary>
    private async Task ExecuteOnServerAsync(string sql, CancellationToken ct = default)
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
        await DropDatabaseAsync(databaseName, ct);
        await CreateDatabaseAsync(databaseName, templateDatabaseName: null, ct);

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
    /// unquoted-safe characters instead: every name in the harness is generated or a constant. Every
    /// rendered name must also carry the reserved <see cref="ManagedDatabasePrefix"/>, so no DDL can
    /// ever reach a database the harness does not own.
    /// </summary>
    public static string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Length is 0 or > 63
            || !name.All(static character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new ArgumentException($"'{name}' is not a valid test database identifier.", nameof(name));
        }

        RequireManagedDatabaseName(name);

        return $"\"{name}\"";
    }

    /// <summary>The loopback test of one host entry: an IP in <c>127.0.0.0/8</c>/<c>::1</c>, the name
    /// <c>localhost</c>, or a Unix-domain-socket path (which is local by definition). Npgsql's
    /// per-host port syntax is accepted too — <c>127.0.0.1:5432</c>, <c>[::1]:5432</c>,
    /// <c>localhost:5432</c> — because a port never changes where the connection goes.</summary>
    private static bool IsLoopbackHost(string host)
    {
        if (host.StartsWith('/'))
        {
            return true;
        }

        var (candidate, port) = SplitHostAndPort(host);

        // A `:` suffix that is not a plain port number is not a host literal this check can verify
        // (`127.0.0.1:notaport`), so it is refused like any other unverifiable host.
        if (port is not null && (port.Length == 0 || !port.All(char.IsAsciiDigit)))
        {
            return false;
        }

        if (candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Npgsql accepts an IPv6 literal in brackets; IPAddress.Parse wants it bare.
        var address = candidate.StartsWith('[') && candidate.EndsWith(']') ? candidate[1..^1] : candidate;

        return IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);
    }

    /// <summary>
    /// Splits the optional Npgsql per-host port suffix from one host entry: <c>[::1]:5432</c> becomes
    /// (<c>[::1]</c>, <c>5432</c>) and <c>127.0.0.1:5432</c> becomes (<c>127.0.0.1</c>, <c>5432</c>).
    /// A bare IPv6 literal (two or more colons, never bracketed) stays whole, because splitting
    /// <c>::1</c> at a colon would be ambiguous.
    /// </summary>
    private static (string Host, string? Port) SplitHostAndPort(string host)
    {
        if (host.StartsWith('['))
        {
            var closing = host.IndexOf(']');
            if (closing < 0)
            {
                return (host, null);
            }

            var rest = host[(closing + 1)..];
            return rest.Length > 0 && rest.StartsWith(':')
                ? (host[..(closing + 1)], rest[1..])
                : (host, null);
        }

        var colon = host.IndexOf(':');
        return colon < 0 || colon != host.LastIndexOf(':')
            ? (host, null)
            : (host[..colon], host[(colon + 1)..]);
    }

    /// <summary>Splits the <c>Host</c> entry of a connection string into its (trimmed) host entries.</summary>
    private static string[] Hosts(string? host) =>
        string.IsNullOrWhiteSpace(host)
            ? []
            : host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Runs one destructive DDL statement, mapping a privilege failure to the named error of
    /// <see cref="DescribeDdlFailure"/>; every other failure keeps its original
    /// <see cref="PostgresException"/>.
    /// </summary>
    private async Task ExecuteDdlAsync(DdlStatement statement, string sql, CancellationToken ct)
    {
        try
        {
            await ExecuteOnServerAsync(sql, ct);
        }
        catch (PostgresException exception) when (DescribeDdlFailure(exception.SqlState, statement) is { } message)
        {
            throw new InvalidOperationException(message, exception);
        }
    }

    /// <summary>
    /// Connection string for a fixture-side helper connection (never pooled). It is the choke point
    /// of every per-database helper (<see cref="ExecuteAsync"/>, <see cref="QueryStringsAsync"/>,
    /// <see cref="TableNamesAsync"/>, the migration readers), so validating the managed prefix here
    /// means no public member can run SQL against a database the harness does not own
    /// (SEC 09.2.1-T2 hardening).
    /// </summary>
    private string HelperConnectionString(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        RequireManagedDatabaseName(databaseName);

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
