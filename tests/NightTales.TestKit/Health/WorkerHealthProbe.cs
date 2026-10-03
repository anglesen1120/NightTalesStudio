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
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.OpenAI;
using NightTales.TestKit.Security;
using NightTales.TestKit.Storage;
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

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = connectionString,

            // 09.3.1-T1: the Worker graph binds and validates these on start-up and this probe owns
            // its own configuration, so the values the Worker's appsettings.json carries are set here.
            [$"{RegistrationOptions.SectionName}:{nameof(RegistrationOptions.Mode)}"] =
                nameof(RegistrationMode.Closed),
            [$"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}"] =
                nameof(BreachedPasswordMode.List),
            [$"{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Tos)}"] = "v1",
            [$"{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Privacy)}"] = "v1",

            // 09.9.4-T1: the Worker's composition root registers the Data Protection key ring too, and
            // it refuses to start without the DP_KEK, so the probe provides the run's throwaway PFX —
            // the same two settings the harness's WorkerHost hands its host.
            [TestKek.PathConfigurationKey] = TestKek.SharedPath,
            [TestKek.PasswordConfigurationKey] = TestKek.SharedPassword,

            // 09.7.2-T1 (09 README BD10/BD12): the blob store's endpoint. A closed loopback port, so the
            // storage prober of this probe fails fast without waiting on a dev container's hostname (see
            // StorageTestEndpoint).
            [$"{StorageSettings.SectionName}:{nameof(StorageSettings.ServiceUrl)}"] = StorageTestEndpoint.Unreachable,
        };

        // 09.2.6-T3: the Worker's composition root registers the e-mail pipeline, which validates the
        // `Email` section on start-up, so the probe supplies what the Worker's appsettings.json carries —
        // the same settings the harness's WorkerHost hands its host.
        foreach (var (key, value) in EmailTestSettings.WorkerSettings)
        {
            settings[key] = value;
        }

        // 02.2.1-T2: OpenAi:ApiKey is required by the Worker's OpenAI text adapter.
        foreach (var (key, value) in OpenAiTestSettings.WorkerSettings)
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);

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

        // 09.9.3-T1: the readiness probe also reports the render-node registry, which is Degraded while
        // the cache holds no answering node (09.9-D2). The poller is not part of this probe, so it
        // publishes the one healthy answer a running poller would, and a test's own status is the
        // storage or database's.
        app.Services.GetRequiredService<NightTales.Application.Abstractions.Rendering.INodeHealthCache>()
            .Set(new NightTales.Application.Abstractions.Rendering.NodeHealthSnapshot(
                "probe-node",
                FakeClock.DefaultStart,
                HttpOk: true,
                Status: "ok",
                Reasons: [],
                MaxConcurrentTasks: 1,
                ActiveTasks: 0,
                Accepting: true,
                GpuUtilPct: null,
                GpuMemUsedMb: null,
                DiskUsedPct: null));

        await app.StartAsync(cancellationToken);

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new WorkerHealthProbe(app, new Uri(address));
    }

    /// <summary>A client over the probe's loopback listener.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = _baseAddress };

    /// <summary>The started host's services, for a test that must read (or freeze) a cache the probe reports.</summary>
    public IServiceProvider Services => _app.Services;

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
