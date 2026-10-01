using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// Registers the public asset URL builder of the catalogs (09.4.2-T1, plan §3/step 6).
/// </summary>
/// <remarks>
/// Both composition roots call this — the Api maps the catalog routes, and the Worker must be able to
/// compose the same URL for its email/publish templates — so the two hosts never disagree about what a
/// <c>preview_storage_key</c> points at.
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
