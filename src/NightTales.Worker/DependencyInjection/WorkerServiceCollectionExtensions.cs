using System.Reflection;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.Infrastructure.Persistence;
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

        services.AddPersistence(configuration);
        services.AddNightTalesMediator();
        services.AddJobs(configuration, [typeof(WorkerServiceCollectionExtensions).Assembly, .. assemblies]);

        // 09.2.4-T1 appends the hosted OutboxDispatcher inside this composition root (09 W3 CR-42).

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
