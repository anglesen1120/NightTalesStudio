extern alias NightTalesWorkerHost;

using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit.Database;
using NightTales.TestKit.Security;
using NightTales.TestKit.Time;
using NightTalesWorkerHost::NightTales.Worker.DependencyInjection;
using WorkerComposition = NightTalesWorkerHost::NightTales.Worker.DependencyInjection.WorkerServiceCollectionExtensions;

namespace NightTales.TestKit.Threading;

/// <summary>
/// Runs Worker code in-process against a test database (09 README D13): it builds the production
/// service collection through the Worker's own <c>AddWorkerServices</c> composition root, with the
/// class database's connection string and the <see cref="FakeClock"/> in place of the system clock.
/// </summary>
/// <remarks>
/// <para>
/// Two modes (09 W3 CR-42 / WRK-6, WRK-7). <b>Inline</b> is the default: <see cref="Jobs"/> is an
/// <see cref="InlineJobScheduler"/> and no Hangfire server starts, so a test drives jobs itself.
/// <b>Real-server</b> (<see cref="WithHangfireServer"/>) adds the production Hangfire server with a
/// one-second queue and schedule poll, for the enqueue, queue-routing and recurring tests only.
/// </para>
/// <para>
/// The host is not started by the constructor: call <see cref="StartAsync"/> so a test can add its
/// probe jobs, registrations and settings first. Nothing here sleeps or polls a clock.
/// </para>
/// </remarks>
/// <param name="database">The database of the test class; its connection string is handed to the host.</param>
/// <param name="clock">The clock every component resolves instead of the system clock.</param>
public sealed class WorkerHost(TestDatabase database, FakeClock clock) : IAsyncDisposable
{
    private readonly Dictionary<string, string?> _settings = HarnessSettings(database);

    private readonly List<IRecurringJobRegistration> _recurringJobs = [];
    private readonly List<Action<IServiceCollection>> _configurations = [];
    private readonly List<Assembly> _jobAssemblies = [typeof(WorkerHost).Assembly];
    private bool _withHangfireServer;
    private bool _started;
    private IHost? _host;
    private InlineJobScheduler? _inlineScheduler;

    private const string OneSecond = "1";

    /// <summary>The class database this host runs against.</summary>
    public TestDatabase Database { get; } = database;

    /// <summary>Observes the Hangfire state machine of this host's jobs, so a test never sleeps.</summary>
    public JobStateObserver Observer { get; } = new();

    /// <summary>Whether this host adds the real Hangfire server.</summary>
    public bool UsesHangfireServer => _withHangfireServer;

    /// <summary>
    /// The inline scheduler of the default mode; it is the host's <see cref="IJobScheduler"/> there.
    /// Real-server mode has no inline scheduler — resolve <see cref="Scheduler"/> instead.
    /// </summary>
    public InlineJobScheduler Jobs =>
        _inlineScheduler
        ?? throw new InvalidOperationException(
            "This WorkerHost runs a real Hangfire server, so there is no InlineJobScheduler; use Scheduler.");

    /// <summary>The started host's service provider.</summary>
    public IServiceProvider Services => (_host ?? throw new InvalidOperationException("Start the host before using its services.")).Services;

    /// <summary>The host's job port: the inline scheduler, or the Hangfire adapter in real-server mode.</summary>
    public IJobScheduler Scheduler => Services.GetRequiredService<IJobScheduler>();

    /// <summary>Adds the production Hangfire server with a one-second poll (D13, real-server mode).</summary>
    public WorkerHost WithHangfireServer()
    {
        EnsureNotStarted();
        _withHangfireServer = true;

        return this;
    }

    /// <summary>Registers one recurring job for this host's start (09 README D12).</summary>
    public WorkerHost WithRecurringJobRegistration(IRecurringJobRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        EnsureNotStarted();
        _recurringJobs.Add(registration);

        return this;
    }

    /// <summary>Scans one more assembly for job classes and recurring registrations.</summary>
    public WorkerHost WithJobAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureNotStarted();
        _jobAssemblies.Add(assembly);

        return this;
    }

    /// <summary>Sets one configuration value on top of the harness defaults.</summary>
    public WorkerHost WithSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureNotStarted();
        _settings[key] = value;

        return this;
    }

    /// <summary>Adds services (probe jobs, fakes) to the host's collection.</summary>
    public WorkerHost ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotStarted();
        _configurations.Add(configure);

        return this;
    }

    /// <summary>Builds and starts the host: hosted services, and the Hangfire server in real-server mode.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _host ??= Build();

        try
        {
            await _host.StartAsync(cancellationToken);
        }
        catch
        {
            // A failed start can leave hosted services of a partially started host running — the
            // process-global Hangfire log provider, a Hangfire server — so they are stopped best effort
            // before the boot error leaves this method. A secondary failure from that stop must not
            // replace the error the caller is about to assert, so it is dropped.
            try
            {
                await _host.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // The start failure is the error of interest.
            }

            throw;
        }

        _started = true;
    }

    /// <summary>A scope over the started host, for resolving jobs and services the way Hangfire does.</summary>
    public IServiceScope CreateScope()
    {
        _ = Services;

        return Services.CreateScope();
    }

    /// <summary>
    /// Claims and delivers one outbox batch with the host's own dispatcher — the same
    /// <c>ProcessBatchAsync</c> its loop calls, so a test drives delivery without sleeping (09 README D13).
    /// </summary>
    /// <returns>How many messages the batch claimed; <c>0</c> when nothing was due.</returns>
    public async Task<int> RunOutboxOnceAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = Services.GetRequiredService<OutboxDispatcher>();

        return await dispatcher.ProcessBatchAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_host is null)
        {
            return;
        }

        // Only a fully started host is stopped here: a failed start was already stopped best effort by
        // StartAsync, which rethrew the boot error.
        if (_started)
        {
            await _host.StopAsync();
        }

        _host.Dispose();
        _host = null;
        _started = false;
        _inlineScheduler = null;
    }

    private IHost Build()
    {
        // The Worker graph installs the Hangfire schema on start (PrepareSchemaIfNecessary), so the
        // harness only points it at a guarded, harness-managed database (SEC 09.2.6-T1). A blank value
        // keeps the composition root's own fail-fast error.
        PostgresFixture.RequireManagedWorkerConnectionString(
            _settings[$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"]);

        var builder = Host.CreateApplicationBuilder();

        // The harness owns the configuration: the Worker's appsettings.json must not leak into a test.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(_settings);

        builder.Services.RemoveAll<IClock>();
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddWorkerServices(builder.Configuration, [.. _jobAssemblies]);

        // 09.8.1-T2: the production Worker calls AddRealtime too (its outbox handlers are producers and
        // the hub-ticket sweep reads the `Realtime` section from here), so the harness composes the same
        // graph instead of a Worker that is missing a registration the real one has.
        builder.Services.AddRealtime(builder.Configuration);

        // 09.2.4-T1, D13: RunOutboxOnceAsync is the harness's only outbox delivery path, so the Worker's
        // loop is dropped from the collection before the host is built — it could otherwise claim and deliver
        // a row the moment it is started. The dispatcher singleton stays, which is what RunOutboxOnceAsync
        // drives.
        WorkerComposition.WithoutOutboxDispatcherLoop(builder.Services);

        // The host's filter chain, with the observer first: it must be the last IJobFilterProvider
        // registration (AddJobs registers the platform chain), so Hangfire's client and server both
        // see the observer plus the platform filters.
        builder.Services.AddSingleton<IJobFilterProvider>(
            _ => JobFilterPipeline.Compose(Observer.AsFilterProvider()));

        if (_withHangfireServer)
        {
            builder.Services.AddWorkerHangfireServer();
        }
        else
        {
            // D13: inline is the default — the job port runs jobs in-process and no server starts.
            builder.Services.RemoveAll<IJobScheduler>();
            builder.Services.AddSingleton<InlineJobScheduler>();
            builder.Services.AddSingleton<IJobScheduler>(provider => provider.GetRequiredService<InlineJobScheduler>());
        }

        foreach (var registration in _recurringJobs)
        {
            builder.Services.AddSingleton(registration);
        }

        foreach (var configure in _configurations)
        {
            configure(builder.Services);
        }

        var host = builder.Build();
        _inlineScheduler = _withHangfireServer ? null : host.Services.GetRequiredService<InlineJobScheduler>();
        Observer.Attach(host.Services.GetRequiredService<JobStorage>());

        return host;
    }

    private void EnsureNotStarted()
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("The host is already built; configure it before StartAsync.");
        }
    }

    /// <summary>
    /// The harness configuration: the class database, one-second polls, and one worker per queue —
    /// the minimal test server of 09.2.6 plan §9 (the production default is 4/2/1/1 per queue).
    /// </summary>
    private static Dictionary<string, string?> HarnessSettings(TestDatabase database)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = database.ConnectionString,
            [$"{JobsOptions.SectionName}:{nameof(JobsOptions.QueuePollSeconds)}"] = OneSecond,
            [$"{JobsOptions.SectionName}:{nameof(JobsOptions.SchedulePollSeconds)}"] = OneSecond,

            // 09.3.1-T1: the Worker graph binds and validates these on start-up, and this harness owns
            // the configuration (the Worker's appsettings.json must not leak into a test).
            [$"{RegistrationOptions.SectionName}:{nameof(RegistrationOptions.Mode)}"] =
                nameof(RegistrationMode.Closed),
            [$"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}"] =
                nameof(BreachedPasswordMode.List),
            [$"{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Tos)}"] = "v1",
            [$"{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Privacy)}"] = "v1",

            // 09.9.4-T1: the Worker's composition root registers the Data Protection key ring too, so the
            // harness provides the run's throwaway KEK exactly as a deployment's secret store does.
            [TestKek.PathConfigurationKey] = TestKek.SharedPath,
            [TestKek.PasswordConfigurationKey] = TestKek.SharedPassword,
        };

        foreach (var queue in Enum.GetValues<JobQueue>())
        {
            settings[$"{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}:{JobQueues.Name(queue)}"] = OneSecond;
        }

        return settings;
    }
}
