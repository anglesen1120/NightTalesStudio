using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NightTales.Application.Abstractions;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The design-time seam of <see cref="AppDbContext"/> (09.2.2-T2): <c>dotnet ef</c> builds the model
/// from here instead of booting the Api host, so the tools need no web server, no authentication and
/// no <c>appsettings</c> file. The connection string comes from the environment exactly as the host
/// reads it, and the actor is <see cref="AnonymousCurrentUser"/> — the same scope the composition
/// root of 09.2.1-T1 registers until 09.2.3-T1 replaces it — so the design-time model carries the
/// production model, tenant filter included.
/// </summary>
/// <remarks>
/// Migrations and the model snapshot are generated from this factory, so its options must stay
/// equivalent to <c>AddPersistence</c>: Npgsql plus the snake_case naming convention (the naming
/// convention changes every table, column, key and index name the migration writes), with the
/// migrations assembly defaulting to this one — the assembly of the context.
/// </remarks>
public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// The environment variable the tools read the connection string from — the same value the host
    /// reads as <c>ConnectionStrings:Postgres</c> (11 CR-14). It is never passed on the command line:
    /// a connection string in argv is visible to every process on the machine.
    /// </summary>
    public const string ConnectionStringVariable = "ConnectionStrings__Postgres";

    /// <summary>
    /// The non-secret fallback the factory uses when neither environment variable is set. It is never
    /// contacted: the factory only builds the model, and a consumer that really opens a connection supplies
    /// its own value — the bundled runner (<c>nts-migrate</c>) applies its <c>--connection</c> argument after
    /// the context has been built, so this placeholder is discarded before any socket is opened.
    /// </summary>
    public const string PlaceholderConnectionString = "Host=invalid.localhost;Database=nts_design_time";

    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? Environment.GetEnvironmentVariable("ConnectionStrings:Postgres");

        // A missing connection string is not an error at design time. `dotnet ef` and the bundled runner need
        // the model, not a reachable server, and the bundle applies its `--connection` value only after the
        // context is built. Throwing here would make `nts-migrate --connection "<db>"` fail before it could
        // read its own argument.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = PlaceholderConnectionString;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new AnonymousCurrentUser());
    }
}
