using System.Reflection;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Features.Episodes.Outbox;
using NightTales.Domain.Episodes.Events;
using NightTales.Domain.Platform.Events;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Email;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Flags;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Security;
using NightTales.Infrastructure.Storage;
using NightTales.Worker.Health;

namespace NightTales.Worker.DependencyInjection;

/// <summary>
/// The one Worker composition root (09 README D13, 09 W3 CR-42): <c>Worker/Program.cs</c> and the
/// test harness's <c>WorkerHost</c> both build the service collection from here, so the harness runs
/// exactly the production graph — persistence, Mediator, jobs, the recurring scan and the health
/// checks — and only the Hangfire server is added separately (production always, tests in real-server
/// mode only).
/// </summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// The factory of the hosted descriptor that runs the outbox loop (09.2.4-T1): it resolves the
    /// dispatcher singleton, so the loop and <c>WorkerHost.RunOutboxOnceAsync</c> drive one instance.
    /// </summary>
    /// <remarks>
    /// A named field rather than a lambda so <see cref="WithoutOutboxDispatcherLoop"/> can find exactly this
    /// descriptor by reference — the compiler-generated lambda of a method body is not addressable.
    /// </remarks>
    public static readonly Func<IServiceProvider, IHostedService> OutboxDispatcherLoopFactory =
        provider => provider.GetRequiredService<OutboxDispatcher>();

    /// <summary>Registers everything the Worker needs except the Hangfire server.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="assemblies">
    /// Extra assemblies to scan for job classes and recurring registrations, on top of this assembly
    /// and the Application assembly (the harness passes the assembly that declares its probe jobs).
    /// </param>
    public static IServiceCollection AddWorkerServices(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assemblies);

        // 02.2.3-T1 (02 README D5/F22): the Worker writes stories, so it loads and validates the prompt
        // catalog at start-up like the Api. Registered before the rest of the graph so its gate is the
        // first hosted service to start: a catalog that does not match its manifest must fail the boot
        // before anything else does work.
        services.AddPromptCatalog();

        services.AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .Validate(
                options => options.HealthPort is > 0 and <= 65535,
                $"'{WorkerOptions.SectionName}:{nameof(WorkerOptions.HealthPort)}' must be a TCP port between 1 and 65535.")
            .ValidateOnStart();

        services.AddPersistence(configuration, assemblies);

        // 09.9.2-T2 (09 README BD13): the same feature-flag read service the Api composes — the cached port
        // a job or outbox handler checks, the writer's NOTIFY notifier, and this instance's
        // `LISTEN feature_flags_changed` invalidator, so a flip reached on one host is believed on all.
        services.AddFeatureFlags(configuration);

        // 09.9.4-T1 (09 README BD12/WF15): the Worker protects and reads back values with the same ring
        // and the same DP_KEK as the Api (11 §10:284), so a token the Api protected — a channel
        // connection, a settings secret — is readable here, and each purpose stays isolated.
        services.AddNightTalesDataProtection(configuration);

        // 09.2.3-T1 (09 README D10): the Worker's actor is the scoped system context. Every user-owned
        // row stays invisible until a handler calls ISystemContext.For(userId), and both interfaces
        // resolve to the same instance so the filter and the ordering guard share one state.
        services.RemoveAll<ICurrentUser>();
        services.RemoveAll<ISystemContext>();
        services.AddScoped<ScopedCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ScopedCurrentUser>());
        services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<ScopedCurrentUser>());

        services.AddNightTalesMediator();
        services.AddJobs(configuration, [typeof(WorkerServiceCollectionExtensions).Assembly, .. assemblies]);

        // 05.5.1-T2 (05 README D10): the render-quality thresholds the ingest job applies to a measured
        // `render-check.v1` are configuration, because 05.2.1-T1/05.5.2-T1 calibrate them; they are
        // validated on start so an impossible policy fails the boot, not the first render.
        services.AddRenderQuality(configuration);

        // 09.4.2-T1 (plan §3 step 6): the Worker composes the same public catalog URL as the Api, so a
        // template it renders or mails never disagrees with the Api about what a storage key points at.
        services.AddPublicAssetUrl();

        // 09.2.6-T3 (09 README BD9): the e-mail pipeline — the validated template set, the renderer, the
        // SMTP adapter and the `email_requested.v1` consumer that enqueues SendEmailJob. The Api registers
        // the same graph (it is the producer side); the Worker is the host that actually sends.
        services.AddEmail(configuration);

        // 09.5.1-T1 (09 README D11/D16, F14): the one consumer of `episode_cancelled.v1`. It runs in the
        // delivery's own scope — already pinned to the event's user — and dispatches a command through
        // ISender, so the D4 pipeline and its transaction own the asset update. It lives in the
        // composition root and not in AddPersistence because a Worker-only consumer has no business in
        // the Api host's outbox path (the Api only writes outbox rows, 09 README D12).
        services.AddOutboxHandler<EpisodeCancelled, EpisodeCancelledAssetRetentionHandler>();

        // 09.9.2-T2 (09 README BD13): the `feature_flags_changed.v1` consumer — it drops the key from this
        // Worker instance's flag cache. It is idempotent and writes nothing: the writer owns the row, the
        // audit row and the NOTIFY; this handler is the outbox half of the two eviction paths.
        services.AddOutboxHandler<FeatureFlagsChanged, FeatureFlagsChangedHandler>();

        // 09.2.4-T1 appends the hosted OutboxDispatcher inside this composition root (09 W3 CR-42).
        services.AddOptions<OutboxDispatcherOptions>()
            .BindConfiguration(OutboxDispatcherOptions.SectionName)
            .Validate(
                options => options.PollIntervalSeconds > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.PollIntervalSeconds)}' must be greater than zero.")
            .Validate(
                options => options.BatchSize > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.BatchSize)}' must be greater than zero.")
            .Validate(
                options => options.LeaseMinutes > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.LeaseMinutes)}' must be greater than zero.")
            .Validate(
                options => options.MaxAttempts > 0,
                $"'{OutboxDispatcherOptions.SectionName}:{nameof(OutboxDispatcherOptions.MaxAttempts)}' must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<OutboxMetrics>();

        // Registered as a singleton and hosted through that instance, so a host and the harness resolve the
        // very dispatcher the Worker's loop owns (WorkerHost.RunOutboxOnceAsync, 09 README D13).
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<IHostedService>(OutboxDispatcherLoopFactory);

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<HangfireStorageHealthCheck>("hangfire");

        return services;
    }

    /// <summary>
    /// Drops the hosted outbox loop from <paramref name="services"/> and leaves the dispatcher singleton in
    /// place (09 README D13): the test harness drives delivery through <c>WorkerHost.RunOutboxOnceAsync</c>,
    /// so the loop must never start.
    /// </summary>
    /// <remarks>
    /// The descriptor has to go before the host is built. Stopping the loop after the start is not enough:
    /// <c>BackgroundService.StartAsync</c> schedules the loop, so it can claim and deliver a row before any
    /// stop reaches it. Removing it here leaves no window at all.
    /// </remarks>
    /// <param name="services">The service collection of the host.</param>
    public static IServiceCollection WithoutOutboxDispatcherLoop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var loop = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && ReferenceEquals(descriptor.ImplementationFactory, OutboxDispatcherLoopFactory));

        if (loop is not null)
        {
            services.Remove(loop);
        }

        return services;
    }

    /// <summary>
    /// Adds the single Hangfire server of the platform (09.2.6 plan §7): one server on every queue,
    /// with the worker counts and poll intervals of the <c>Jobs</c> section. Production calls this from
    /// <c>Program.cs</c>; the test harness calls it only in real-server mode (D13).
    /// </summary>
    public static IServiceCollection AddWorkerHangfireServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddHangfireServer((provider, options) =>
        {
            var jobs = provider.GetRequiredService<IOptions<JobsOptions>>().Value;

            options.Queues = [.. JobQueues.All];
            options.WorkerCount = jobs.TotalWorkerCount;
            options.SchedulePollingInterval = TimeSpan.FromSeconds(jobs.SchedulePollSeconds);
        });
    }
}
