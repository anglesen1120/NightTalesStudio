using System.Reflection;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Security;
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

        services.AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .Validate(
                options => options.HealthPort is > 0 and <= 65535,
                $"'{WorkerOptions.SectionName}:{nameof(WorkerOptions.HealthPort)}' must be a TCP port between 1 and 65535.")
            .ValidateOnStart();

        services.AddPersistence(configuration, assemblies);

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

        // Registered as a singleton and hosted through that instance, so the harness can drive the very
        // dispatcher the Worker's loop owns (WorkerHost.RunOutboxOnceAsync, 09 README D13).
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService<OutboxDispatcher>(provider => provider.GetRequiredService<OutboxDispatcher>());

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<HangfireStorageHealthCheck>("hangfire");

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
