using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
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

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

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

        // Temporary: 09.2.3-T1 replaces this with HttpCurrentUser (Api) and ScopedCurrentUser (Worker).
        services.AddScoped<ICurrentUser, AnonymousCurrentUser>();

        // The audit interceptor stamps every ICreatedAt/IAuditable row from this clock (09 W3 CR-38).
        // TryAdd, so a host may replace the clock (the tests do, with FakeClock).
        services.TryAddSingleton<IClock, SystemClock>();

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(
                connectionString,
                npgsql => npgsql
                    .CommandTimeout(OptionsOf(provider).CommandTimeoutSeconds)
                    .MigrationsAssembly(MigrationsAssemblyName))
            .UseSnakeCaseNamingConvention()
            // Order matters (09 README D8, 09 W3 CR-38): the auditable interceptor runs first and
            // OutboxSaveChangesInterceptor (09.2.4-T1) is added after it, inside the same SaveChanges.
            .AddInterceptors(new AuditableSaveChangesInterceptor(provider.GetRequiredService<IClock>())));

        services.AddDbContext<ReadDbContext>((provider, options) => options
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.CommandTimeout(OptionsOf(provider).CommandTimeoutSeconds))
            .UseSnakeCaseNamingConvention()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<IReadDb>(provider => provider.GetRequiredService<ReadDbContext>());

        return services;
    }

    private static PersistenceOptions OptionsOf(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
}
