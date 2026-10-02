using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// Registers the storage services of a host: the blob store of 09.7.2-T1 (step 2/6) — the bound
/// <c>Storage:*</c> settings, the clients, the port, the health cache and the prober — and the public
/// asset URL builder of the catalogs (09.4.2-T1, plan §3/step 6).
/// </summary>
/// <remarks>
/// <para>
/// Both composition roots call both methods: the Api presigns the episode video URL and maps the catalog
/// routes, and the Worker runs the retention sweep, the render ingest of 09.6.3-T2 and 02's fetcher path
/// — so a host that can serve an episode file is the same host that can sweep it, and the two hosts
/// never disagree about what a <c>preview_storage_key</c> points at.
/// </para>
/// <para>
/// <strong>What <see cref="AddStorage"/> validates at start-up, and what it does not.</strong> The
/// endpoint, the bucket names, the marker key, the two TTL ranges and the probe interval are validated
/// when the options are first resolved at start-up (<c>ValidateOnStart</c>): each of them is a value no
/// deployment can work without and none of them is a secret. The access and secret key are deliberately
/// <em>not</em> validated there: the build-time OpenAPI generator boots the Api with the checked-in
/// <c>appsettings.json</c> alone (09.1.5-T1) and the CI build step exports only the connection string and
/// the signing key, so a credential check would fail every build for a value that lives in the secret
/// manager (11 D15). A missing credential is not silent either: every operation answers 503
/// (<c>blob_store_operations_total{outcome="denied"}</c>) and the <c>storage</c> readiness component
/// reports degraded with the prober logging at <c>Warning</c>.
/// </para>
/// </remarks>
public static class StorageServiceCollectionExtensions
{
    /// <summary>Binds <see cref="StorageOptions"/> and registers <see cref="IPublicAssetUrl"/>.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddPublicAssetUrl(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);

        // TryAdd: a host (or a test) that brings its own builder keeps it.
        services.TryAddSingleton<IPublicAssetUrl, ConfigurationPublicAssetUrl>();

        // One warning at startup when the host has no public bucket, never one per request (plan §3).
        services.AddHostedService<PublicAssetUrlWarning>();

        return services;
    }

    /// <summary>
    /// The factory of the hosted descriptor that runs the storage prober, held in a field so a test can
    /// remove exactly this descriptor from a collection by reference and drive
    /// <see cref="StorageHealthProbe.ProbeAsync"/> itself (09 README D13; the
    /// <c>OutboxDispatcherLoopFactory</c> pattern).
    /// </summary>
    public static readonly Func<IServiceProvider, IHostedService> HealthProbeFactory =
        provider => provider.GetRequiredService<StorageHealthProbe>();

    /// <summary>Registers the blob store and its health prober.</summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configuration">The host configuration; it carries the <c>Storage</c> section.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StorageSettings>()
            .BindConfiguration(StorageSettings.SectionName)
            .Validate(
                options => IsAbsoluteHttpUrl(options.ServiceUrl),
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.ServiceUrl)}' must be an absolute http(s) URL.")
            .Validate(
                options => options.PublicServiceUrl is null || IsAbsoluteHttpUrl(options.PublicServiceUrl),
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.PublicServiceUrl)}' must be an absolute http(s) URL when it is set.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.HealthMarkerKey),
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.HealthMarkerKey)}' must not be blank.")
            .Validate(
                options => options.Buckets is not null
                    && !string.IsNullOrWhiteSpace(options.Buckets.Media)
                    && !string.IsNullOrWhiteSpace(options.Buckets.Public)
                    && !string.IsNullOrWhiteSpace(options.Buckets.Music),
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.Buckets)}' must name the media, public and music buckets.")
            .Validate(
                options => options.PresignTtlMinutes >= 1,
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.PresignTtlMinutes)}' must be at least one minute (it is clamped to {StorageSettings.MaxPresignTtlMinutes} when read).")
            .Validate(
                options => options.TikTokPresignTtlMinutes is >= 1 and <= 120,
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.TikTokPresignTtlMinutes)}' must be between 1 and 120 minutes.")
            .Validate(
                options => options.HealthProbeSeconds >= 1,
                $"'{StorageSettings.SectionName}:{nameof(StorageSettings.HealthProbeSeconds)}' must be at least one second.")
            .ValidateOnStart();

        services.AddSingleton<StorageMetrics>();
        services.AddSingleton<S3ClientFactory>();
        services.AddSingleton<IBlobStore, S3BlobStore>();
        services.AddSingleton<IStorageHealthCache, StorageHealthCache>();

        // The prober is registered through the named factory (not AddHostedService) so the harness can
        // remove this descriptor by reference; the readiness check itself reads the cache only.
        services.AddSingleton<StorageHealthProbe>();
        services.AddSingleton<IHostedService>(HealthProbeFactory);

        return services;
    }

    /// <summary>
    /// Drops the hosted storage prober from <paramref name="services"/> (09 README D13), so a test can
    /// drive <see cref="StorageHealthProbe.ProbeAsync"/> itself or freeze the health cache. The
    /// descriptor is found by the reference of <see cref="HealthProbeFactory"/>, the same rule the
    /// Worker harness uses for the outbox loop.
    /// </summary>
    /// <param name="services">The collection to change; it is modified in place.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection WithoutStorageHealthProbe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];

            if (descriptor.ServiceType == typeof(IHostedService)
                && ReferenceEquals(descriptor.ImplementationFactory, HealthProbeFactory))
            {
                services.RemoveAt(index);
            }
        }

        return services;
    }

    private static bool IsAbsoluteHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// Says once, at startup, that every catalog <c>previewUrl</c> will be <see langword="null"/> because
/// <c>Storage:PublicBaseUrl</c> is empty (09.4.2-T1 plan §3: the behaviour is allowed in Development
/// and misconfiguration elsewhere, but it must not be silent).
/// </summary>
/// <param name="options">The <c>Storage</c> section.</param>
/// <param name="logger">The startup warning.</param>
internal sealed partial class PublicAssetUrlWarning(
    IOptions<StorageOptions> options,
    ILogger<PublicAssetUrlWarning> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl))
        {
            LogMissingBaseUrl();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Warning,
        Message = "storage.public-asset-url.missing-base-url: Storage:PublicBaseUrl is empty, every catalog previewUrl is null")]
    private partial void LogMissingBaseUrl();
}
