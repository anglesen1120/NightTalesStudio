using System.Reflection;
using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// The Hangfire composition of the platform (09 README D12/D13, 09.2.6-T1 steps 1–2): storage and
/// client, the <see cref="IJobScheduler"/> adapter, the <see cref="JobPolicyAttribute"/> retry policy,
/// the recurring-job reconciliation and the job-class scan.
/// </summary>
/// <remarks>
/// It requires the persistence layer of the same host (<c>AddPersistence</c>) because the storage
/// shares the platform database and its connection string. It never starts a Hangfire server: the
/// Worker adds one (<c>AddWorkerHangfireServer</c>) and <c>WorkerHost</c> adds it only in real-server
/// mode, so the test harness runs the production graph minus the server (D13).
/// </remarks>
public static class JobsServiceCollectionExtensions
{
    /// <summary>Registers the Hangfire client half of the Worker and every job found in the scanned assemblies.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration; it carries <c>ConnectionStrings:Postgres</c>.</param>
    /// <param name="assemblies">
    /// Extra assemblies to scan for <see cref="IBackgroundJob"/> and
    /// <see cref="IRecurringJobRegistration"/> types, on top of the Application assembly.
    /// </param>
    public static IServiceCollection AddJobs(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assemblies);

        var connectionString = RequireConnectionString(configuration);

        services.AddOptions<JobsOptions>()
            .BindConfiguration(JobsOptions.SectionName)
            .Validate(
                options => options.QueuePollSeconds > 0,
                $"'{JobsOptions.SectionName}:{nameof(JobsOptions.QueuePollSeconds)}' must be greater than zero.")
            .Validate(
                options => options.SchedulePollSeconds > 0,
                $"'{JobsOptions.SectionName}:{nameof(JobsOptions.SchedulePollSeconds)}' must be greater than zero.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.SchemaName),
                $"'{JobsOptions.SectionName}:{nameof(JobsOptions.SchemaName)}' must not be blank.")
            .Validate(
                options => options.Workers.Values.All(count => count > 0),
                $"every '{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}:<queue>' count must be greater than zero.")
            .Validate(
                options => options.UnknownQueueNames().Count == 0,
                $"'{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}' may only name queues of "
                + $"[{string.Join(", ", JobQueues.All)}].")
            .ValidateOnStart();

        // Each host owns its storage instance. AddHangfire's own JobStorage registration resolves the
        // process-static JobStorage.Current, which two hosts in one process — the Worker and the test
        // harness, or two parallel test classes — would otherwise fight over, sending one host's jobs
        // to the other host's database.
        services.AddSingleton<JobStorage>(provider => CreateStorage(provider, connectionString));

        // For the same reason, each host owns its JobActivator. Every host's AddHangfire global
        // configuration calls UseActivator, which overwrites the process-static JobActivator.Current,
        // and BackgroundJobServer falls back to it (activator ?? options.Activator ??
        // JobActivator.Current) whenever the server reads the activator later than another host wrote
        // it. Registering it here — before AddHangfire, whose TryAddSingleton is then skipped — makes
        // the server's options.Activator this host's scope factory, so a job always activates in the
        // container that scheduled it.
        services.TryAddSingleton<JobActivator>(provider =>
            new AspNetCoreJobActivator(provider.GetRequiredService<IServiceScopeFactory>()));

        services.AddHangfire((provider, global) => global.UseStorage(provider.GetRequiredService<JobStorage>()));

        // The platform retry policy: drop Hangfire's global 10-attempt default (once per process) and
        // make the host's own filter chain the one Hangfire resolves — the client and the server both
        // take IJobFilterProvider from DI, and this registration is deliberately last (AddHangfire uses
        // TryAddSingleton) so a host can prepend its own providers.
        JobFilterPipeline.RemoveGlobalAutomaticRetryDefault();
        services.AddSingleton<IJobFilterProvider>(_ => JobFilterPipeline.Compose());

        services.TryAddSingleton<IJobScheduler, HangfireJobScheduler>();

        var registry = BackgroundJobRegistry.AddTo(services, assemblies);
        services.AddSingleton(registry);

        // Registered first so its StopAsync runs last (hosted services stop in reverse order): the
        // process-global Hangfire log provider must outlive this host's own shutdown, and must not
        // outlive the container that owns the ILoggerFactory behind it.
        services.AddHostedService<HangfireLogProviderRelease>();
        services.AddHostedService<BackgroundJobActivationCheck>();
        services.AddHostedService<RecurringJobRegistrar>();

        return services;
    }

    /// <summary>
    /// The PostgreSQL storage of this host, built from the <c>Jobs</c> section (09 README D12): schema
    /// <c>hangfire</c>, schema install on start, and the configured queue poll interval.
    /// </summary>
    private static PostgreSqlStorage CreateStorage(IServiceProvider provider, string connectionString)
    {
        var jobs = provider.GetRequiredService<IOptions<JobsOptions>>().Value;
        var options = new PostgreSqlStorageOptions
        {
            SchemaName = jobs.SchemaName,
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(jobs.QueuePollSeconds),
        };

        return new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString, options), options);
    }

    /// <summary>
    /// The platform connection string, with the same fail-fast contract as <c>AddPersistence</c>
    /// (09 README D8): a blank value never reaches Hangfire.
    /// </summary>
    private static string RequireConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new OptionsValidationException(
                nameof(PersistenceOptions),
                typeof(PersistenceOptions),
                [$"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is required and must not be blank."]);
        }

        return connectionString;
    }
}
