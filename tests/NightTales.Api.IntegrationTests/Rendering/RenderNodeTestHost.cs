using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Api.IntegrationTests.Seeding;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// A real service provider over the class database for the render-node tests (11.3.3-T1 plan §7/§9): the
/// production <c>AddRenderNodes</c> registrations plus the persistence ports the seeder, the poller and the
/// <c>nodes</c> verbs resolve, with the test's <see cref="FakeClock"/> and <c>RenderNodes</c> settings in
/// place of configuration. No host is started, so a test drives the pieces it wants directly.
/// </summary>
/// <remarks>
/// It composes the same <c>AddRenderNodes</c> the Worker does rather than hand-writing the graph, so a
/// registration this task forgets fails the tests instead of hiding behind a hand-built container.
/// </remarks>
internal sealed class RenderNodeTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    private RenderNodeTestHost(ServiceProvider provider) => _provider = provider;

    /// <summary>The built provider; a test creates scopes over it exactly as a request or a tick would.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Builds the provider for one test database.</summary>
    /// <param name="database">The class database the rows live in.</param>
    /// <param name="clock">The clock every component resolves, so the poller's hysteresis is deterministic.</param>
    /// <param name="settings">
    /// The configuration values, including <c>ConnectionStrings:Postgres</c>, <c>RenderNodes:Nodes:*</c> and
    /// the per-node <c>RENDER_NODE_&lt;NAME&gt;_API_KEY</c> entries.
    /// </param>
    /// <param name="environmentName">The host environment name; <c>Development</c> unless a test says otherwise.</param>
    /// <param name="configure">Extra registrations, for example the poller or a fake connection factory.</param>
    public static RenderNodeTestHost Build(
        TestDatabase database,
        FakeClock clock,
        IReadOnlyDictionary<string, string?> settings,
        string environmentName = "Development",
        Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var currentUser = new TestCurrentUser();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ICurrentUser>(currentUser);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = environmentName });

        services.AddScoped(_ => TestDbContextFactory.CreateAppContext(database.ConnectionString, currentUser, clock));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped(_ => TestDbContextFactory.CreateReadContext(database.ConnectionString, currentUser));
        services.AddScoped<IReadDb>(provider => provider.GetRequiredService<ReadDbContext>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();

        services.AddRenderNodes(configuration);

        configure?.Invoke(services);

        return new RenderNodeTestHost(services.BuildServiceProvider());
    }

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// Clears the two tables the render-node tests write (11.3.3-T1 plan §9). A class database is shared by
/// every test of its class, so a test that seeds a node named <c>alpha</c> or asserts "exactly one audit
/// row" must start from the empty state the class was created with.
/// </summary>
internal static class RenderNodeDatabase
{
    /// <summary>Deletes every <c>worker_nodes</c> and <c>audit_logs</c> row of the class database.</summary>
    /// <param name="database">The class database.</param>
    public static void Reset(TestDatabase database) =>
        SqlProbe.Execute(database, "DELETE FROM worker_nodes; DELETE FROM audit_logs;");
}
