using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Flags;

namespace NightTales.Infrastructure.Persistence.Flags;

/// <summary>
/// Registers the feature-flag read service (09 README BD13, 09.9.2-T2 step 2): the port, its cached
/// implementation over a 30 s window, the writer's eviction notifier and the per-instance
/// <c>LISTEN</c> invalidator.
/// </summary>
/// <remarks>
/// <para>
/// Both hosts call this. The Api and the Worker each guard work behind flags — the Api through
/// <c>RequireFeature</c>, the Worker through <c>IFeatureFlags</c> in jobs and outbox handlers — and each
/// keeps its own cache, so each needs its own invalidator: a <c>NOTIFY</c> is delivered to every session on
/// the channel, and the eviction is what makes a flip visible on all of them at once.
/// </para>
/// <para>
/// The outbox handler <c>FeatureFlagsChangedHandler</c> is registered by the Worker's composition root,
/// next to its other consumers (09 README D11/R6): the Api only writes outbox rows.
/// </para>
/// </remarks>
public static class FeatureFlagsServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="FeatureFlagsOptions"/>, registers the reader, the cached port, the notifier and the
    /// invalidator.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The host configuration; the <c>FeatureFlags</c> section is read from it.</param>
    public static IServiceCollection AddFeatureFlags(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FeatureFlagsOptions>()
            .BindConfiguration(FeatureFlagsOptions.SectionName)
            .Validate(
                options => options.CacheSeconds > 0,
                $"'{FeatureFlagsOptions.SectionName}:{nameof(FeatureFlagsOptions.CacheSeconds)}' must be greater than "
                + "zero: a zero-second window would read the row on every guarded request.")
            .Validate(
                options => options.HeartbeatSeconds > 0,
                $"'{FeatureFlagsOptions.SectionName}:{nameof(FeatureFlagsOptions.HeartbeatSeconds)}' must be greater "
                + "than zero.")
            .Validate(
                options => options.ListenerBackoffSeconds is { Count: > 0 }
                    && options.ListenerBackoffSeconds.All(seconds => seconds > 0),
                $"'{FeatureFlagsOptions.SectionName}:{nameof(FeatureFlagsOptions.ListenerBackoffSeconds)}' must hold at "
                + "least one positive number of seconds.")
            .ValidateOnStart();

        services.AddMemoryCache();

        services.AddScoped<IFeatureFlagReader, FeatureFlagReader>();
        services.AddScoped<IFeatureFlags, CachedFeatureFlags>();
        services.AddScoped<IFeatureFlagChangeNotifier, PgFeatureFlagChangeNotifier>();

        services.AddSingleton<IFeatureFlagsListenerConnectionFactory, PgFeatureFlagsListenerFactory>();
        services.AddSingleton<IFeatureFlagsListenerDelay, SystemFeatureFlagsListenerDelay>();

        // Hosted through the singleton registration, so a test can await ListeningAsync on the very loop the
        // host started instead of resolving a second instance (the OutboxDispatcher pattern, 09 W3 CR-42).
        services.AddSingleton<FeatureFlagsCacheInvalidator>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FeatureFlagsCacheInvalidator>());

        return services;
    }
}
