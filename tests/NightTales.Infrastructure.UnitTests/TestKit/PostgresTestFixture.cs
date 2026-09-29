using Npgsql;
using NightTales.Application.Abstractions;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.UnitTests.TestKit;

/// <summary>
/// Gives a test class its own database on the PostgreSQL 17 server named by <c>NTS_TEST_PG</c>
/// (09 README D13, 09 W3 CR-35): a plain <c>CREATE DATABASE</c> at construction, the probe schema
/// created once, and <c>DROP DATABASE … WITH (FORCE)</c> at the end. The reusable fixture
/// (<c>PostgresFixture</c> with the <c>nts_template</c> database) is 09.2.1-T2; until it lands this
/// is the per-class database that task file's step 4 allows.
/// </summary>
public sealed class PostgresTestFixture : IDisposable
{
    private const string ServerConnectionStringVariable = "NTS_TEST_PG";

    private readonly string _serverConnectionString;
    private readonly string _databaseName;

    public PostgresTestFixture()
    {
        _serverConnectionString = Environment.GetEnvironmentVariable(ServerConnectionStringVariable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Environment variable '{ServerConnectionStringVariable}' is required: it must hold the connection string of the " +
                "PostgreSQL 17 test server, for example " +
                "'Host=127.0.0.1;Port=5432;Database=nighttales;Username=nighttales;Password=<the dev password from .env>'.");

        _databaseName = $"nts_test_{Guid.NewGuid():N}"[..24];
        ConnectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString)
        {
            Database = _databaseName,
        }.ConnectionString;

        ExecuteOnServer($"CREATE DATABASE \"{_databaseName}\"");

        using var context = CreateAppDbContext();
        context.Database.EnsureCreated();
    }

    public string ConnectionString { get; }

    public AppDbContext CreateAppDbContext()
        => new(ProbeContexts.AppOptions(ConnectionString), new AnonymousCurrentUser());

    public ReadDbContext CreateReadDbContext()
        => new(ProbeContexts.ReadOptions(ConnectionString), new AnonymousCurrentUser());

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        ExecuteOnServer($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
    }

    private void ExecuteOnServer(string sql)
    {
        using var connection = new NpgsqlConnection(_serverConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
