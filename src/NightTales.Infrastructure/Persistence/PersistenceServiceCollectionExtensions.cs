using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Idempotency;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Persistence.Seeding;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.Infrastructure.Time;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// Registers the persistence layer (09 README D8, §3.3): both contexts as scoped services — never
/// pooled, because <c>AppDbContext</c> captures the scoped <see cref="ICurrentUser"/> — plus the
/// <see cref="IAppDbContext"/> / <see cref="IReadDb"/> ports.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    private static readonly string MigrationsAssemblyName = typeof(AppDbContext).Assembly.GetName().Name!;

    /// <summary>
    /// Registers the persistence layer of a host: both contexts, the ports, the transaction and
    /// idempotency stores, the catalog seeders, the system work query and the outbox write side.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration; it carries <c>ConnectionStrings:Postgres</c>.</param>
    /// <param name="outboxEventAssemblies">
    /// Extra assemblies whose <c>[OutboxEvent]</c> types must be dispatchable on top of the Domain's
    /// (09 README D11). Production passes none; the test harness passes the assembly of its probe events.
    /// </param>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] outboxEventAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(outboxEventAssemblies);

        // Fails at start-up, never lazily on the first request (09 README D8, plan §4 UC4).
        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new OptionsValidationException(
                nameof(PersistenceOptions),
                typeof(PersistenceOptions),
                [$"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is required and must not be blank."]);
        }

        try
        {
            _ = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Constant message: a parser message can echo a malformed keyword/value fragment
            // (NTS-SEC-SECRETS-001), so it never reaches the exception.
            throw new OptionsValidationException(
                nameof(PersistenceOptions),
                typeof(PersistenceOptions),
                [$"Configuration value 'ConnectionStrings:{PersistenceOptions.ConnectionStringName}' is not a valid connection string."]);
        }

        services.AddOptions<PersistenceOptions>()
            .BindConfiguration(PersistenceOptions.SectionName)
            .Validate(
                options => options.CommandTimeoutSeconds > 0,
                $"'{PersistenceOptions.SectionName}:{nameof(PersistenceOptions.CommandTimeoutSeconds)}' must be greater than zero.")
            .ValidateOnStart();

        // The default actor is anonymous, so a host that registers none sees no user-owned row — deny
        // by default (09 README D10). The Api replaces it with HttpCurrentUser and the Worker with
        // ScopedCurrentUser; TryAdd keeps this a fallback in either registration order.
        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();

        // The audit interceptor stamps every ICreatedAt/IAuditable row from this clock (09 W3 CR-38).
        // TryAdd, so a host may replace the clock (the tests do, with FakeClock).
        services.TryAddSingleton<IClock, SystemClock>();

        // 09.2.4-T1: the name ↔ type map of the domain events, built here — at composition time — so two
        // events sharing a name fail the start-up instead of a dispatch (09 README D11). The Domain
        // assembly holds the platform's events; a host may add assemblies of its own (the test harness
        // passes its probe events).
        var outboxEvents = new OutboxEventRegistry(outboxEventAssemblies);
        services.AddSingleton(outboxEvents);

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(
                connectionString,
                npgsql => npgsql
                    .CommandTimeout(OptionsOf(provider).CommandTimeoutSeconds)
                    .MigrationsAssembly(MigrationsAssemblyName))
            .UseSnakeCaseNamingConvention()
            // Order matters (09 README D8, 09 W3 CR-38): the auditable interceptor runs first and
            // OutboxSaveChangesInterceptor (09.2.4-T1) is added after it, inside the same SaveChanges.
            .AddInterceptors(
                new AuditableSaveChangesInterceptor(provider.GetRequiredService<IClock>()),
                new OutboxSaveChangesInterceptor(outboxEvents)));

        services.AddDbContext<ReadDbContext>((provider, options) => options
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.CommandTimeout(OptionsOf(provider).CommandTimeoutSeconds))
            .UseSnakeCaseNamingConvention()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<IReadDb>(provider => provider.GetRequiredService<ReadDbContext>());

        // 09.1.3-T2: the command transaction of D5 and the in-transaction idempotency protocol of D6.
        // Scoped, exactly like the context they wrap: the transaction joins this scope's connection and
        // the store reads the open transaction from it, so a claim commits with its command's effects.
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IIdempotencyCleanupStore, IdempotencyCleanupStore>();

        // 09.3.6-T1: the one writer of `audit_logs` (09 README BD15, D9). Scoped with the context it adds
        // to, so the row joins the caller's transaction; `AuditBehavior` is slot 6 of the D4 pipeline and
        // the auth handlers/jobs/CLI verbs call the same port.
        services.AddScoped<IAuditWriter, EfAuditWriter>();

        // The AuditBehavior's per-request-type attribute lookup (09.3.6-T1, plan §3). A singleton, and
        // registered here rather than in a host: both hosts build their Mediator over this persistence
        // layer, and resolving slot 6 without it would fail every command — the Worker never calls
        // AddNightTalesApplication.
        services.TryAddSingleton<AuditMetadataCache>();

        // 09.2.4-T1: the processed-row sweep of the outbox, driven by the Worker's daily recurring job.
        services.AddScoped<IOutboxCleanupStore, OutboxCleanupStore>();

        // 09.2.5-T1: the catalog seeders and the Development auto-seed are part of the persistence
        // layer, so every host that calls AddPersistence (Api, Worker, the test harness) gets them.
        services.AddSeeding();

        // 09.2.3-T1: the only cross-user read outside Features/Admin (09 README D10); both hosts get
        // it from here, because the sweeps that need it run in the Worker.
        services.AddScoped<ISystemWorkQuery, SystemWorkQuery>();

        // 09.3.1-T1: Identity (BD2) + the registration, password-policy and consent services. Every
        // host that reads the write model also authenticates or verifies a caller, so the registration
        // belongs to the same composition root.
        services.AddIdentityServices(configuration);

        return services;
    }

    /// <summary>
    /// Registers the catalog seeders, the runner and the Development auto-seed hosted service
    /// (09.2.5 plan §3.1/§7). The <see cref="SeedOptions"/> section is bound and validated on start.
    /// </summary>
    public static IServiceCollection AddSeeding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<SeedOptions>()
            .BindConfiguration(SeedOptions.SectionName)
            .Validate(
                seed => !string.IsNullOrWhiteSpace(seed.CatalogFile),
                $"'{SeedOptions.SectionName}:{nameof(SeedOptions.CatalogFile)}' must not be blank.")
            .Validate(
                seed => !string.IsNullOrWhiteSpace(seed.StyleTemplatesFile),
                $"'{SeedOptions.SectionName}:{nameof(SeedOptions.StyleTemplatesFile)}' must not be blank.")
            .Validate(
                seed => !string.IsNullOrWhiteSpace(seed.VoiceCatalogFile),
                $"'{SeedOptions.SectionName}:{nameof(SeedOptions.VoiceCatalogFile)}' must not be blank.")
            .ValidateOnStart();

        services.AddScoped<ISeeder, VoiceSeeder>();
        services.AddScoped<ISeeder, StyleTemplateSeeder>();
        services.AddScoped<ISeeder, FeatureFlagSeeder>();
        services.AddScoped<SeedRunner>();
        services.AddHostedService<SeedHostedService>();

        return services;
    }

    private static PersistenceOptions OptionsOf(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
}
