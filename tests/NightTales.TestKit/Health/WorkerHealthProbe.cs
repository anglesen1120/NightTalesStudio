extern alias NightTalesWorkerHost;

using System.Net;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using NightTalesWorkerHost::NightTales.Worker.DependencyInjection;
using NightTalesWorkerHost::NightTales.Worker.Health;
using WorkerHealth = NightTalesWorkerHost::NightTales.Worker.Health.WorkerHealthEndpoints;

namespace NightTales.TestKit.Health;

/// <summary>
/// Runs the Worker's real health pipeline — its <c>AddWorkerServices</c> composition root and its
/// <c>MapWorkerHealth</c> endpoints — on an ephemeral loopback port, so the 09.2.6-T1 readiness
/// contract (200 healthy, 200 degraded, 503 unhealthy) is testable without starting the whole Worker
/// process or stopping the shared PostgreSQL server.
/// </summary>
/// <remarks>
/// The host is the production graph with an unreachable connection string: the recurring-job
/// synchronisation is resilient by design, so the listener still starts and reports. The string is
/// checked by the fixture's destructive-DDL boundary first (<see cref="PostgresFixture"/>
/// <c>.RequireManagedWorkerConnectionString</c>), because the graph installs Hangfire's schema.
/// </remarks>
public sealed class WorkerHealthProbe : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Uri _baseAddress;

    private WorkerHealthProbe(WebApplication app, Uri baseAddress)
    {
        _app = app;
        _baseAddress = baseAddress;
    }

    /// <summary>The liveness path of the probe.</summary>
    public static string LivePath => WorkerHealth.LivePath;

    /// <summary>The readiness path of the probe.</summary>
    public static string ReadyPath => WorkerHealth.ReadyPath;

    /// <summary>Starts the Worker health pipeline against <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">The connection string the Worker is configured with.</param>
    /// <param name="configureServices">Extra services, for example a storage that reports degradation.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<WorkerHealthProbe> StartAsync(
        string connectionString,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        PostgresFixture.RequireManagedWorkerConnectionString(connectionString);

        var builder = WebApplication.CreateBuilder();

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = connectionString,
            });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IClock>(new FakeClock());
        builder.Services.AddWorkerServices(builder.Configuration);

        // The production storage retries its first connection with a one-minute-max backoff (the
        // PostgreSqlStorageOptions defaults), which a probe must not pay: the same PostgreSqlStorage,
        // but failing over in milliseconds while still degrading instead of throwing (the default
        // AllowDegradedModeWithoutStorage), so the listener comes up with the database down.
        builder.Services.RemoveAll<JobStorage>();
        builder.Services.AddSingleton<JobStorage>(CreateStorage(connectionString));

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapWorkerHealth();

        await app.StartAsync(cancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new WorkerHealthProbe(app, new Uri(address));
    }

    /// <summary>A client over the probe's loopback listener.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = _baseAddress };

    /// <summary>The production storage without its startup backoff, so a probe fails over in milliseconds.</summary>
    private static PostgreSqlStorage CreateStorage(string connectionString)
    {
        var options = new PostgreSqlStorageOptions
        {
            SchemaName = JobsOptions.DefaultSchemaName,
            PrepareSchemaIfNecessary = true,
            StartupConnectionMaxRetries = 1,
            StartupConnectionBaseDelay = TimeSpan.FromMilliseconds(10),
            StartupConnectionMaxDelay = TimeSpan.FromMilliseconds(10),
            QueuePollInterval = TimeSpan.FromSeconds(1),
        };

        return new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString, options), options);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
