using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Registers the server core: the bound <see cref="RenderNodeServerOptions"/>, its validator, the clock, the
/// default in-memory store, the capacity gate and the auth throttle.
/// </summary>
/// <remarks>
/// The node-specific seams (<c>IRenderBackend</c>, <c>INodeHealthSource</c>, <c>IImagePlanSink</c>,
/// <c>IImageProbeBackend</c>, and <c>ITaskStore</c> for the adapter) are registered by the host — the mock and
/// the adapter wire different implementations (11 README D5, F02).
/// </remarks>
public static class RenderNodeServerServiceCollectionExtensions
{
    /// <summary>Binds the <c>RenderNodeServer</c> section (11 README D6: <c>RenderNodeServer__ApiKey</c>, …).</summary>
    public static IServiceCollection AddRenderNodeServer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddRenderNodeServer(configuration.GetSection(RenderNodeServerOptions.SectionName));
    }

    /// <summary>Binds an explicit section, for a host that keeps its node settings elsewhere.</summary>
    public static IServiceCollection AddRenderNodeServer(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.AddOptions<RenderNodeServerOptions>().Configure(options => Bind(options, section));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RenderNodeServerOptions>, RenderNodeServerOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITaskStore, InMemoryTaskStore>();
        services.TryAddSingleton<CapacityGate>();
        services.TryAddSingleton(provider => new AuthFailureThrottle(
            provider.GetRequiredService<IOptions<RenderNodeServerOptions>>().Value.AuthFailuresPerMinute,
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Binds by hand instead of through <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>, so the
    /// contract project set stays package-free (11 CR-3) and every key name is one <c>nameof</c> away from its
    /// property.
    /// </summary>
    private static void Bind(RenderNodeServerOptions options, IConfigurationSection section)
    {
        options.ApiKey = section[nameof(RenderNodeServerOptions.ApiKey)] ?? options.ApiKey;

        // An empty or unset NEXT disables the rotation compare; it is never an empty-string match (D15 S3).
        var next = section[nameof(RenderNodeServerOptions.ApiKeyNext)];
        options.ApiKeyNext = string.IsNullOrEmpty(next) ? null : next;

        options.MaxBodyBytes = ReadLong(section, nameof(RenderNodeServerOptions.MaxBodyBytes), options.MaxBodyBytes);
        options.AuthFailuresPerMinute = ReadInt(
            section,
            nameof(RenderNodeServerOptions.AuthFailuresPerMinute),
            options.AuthFailuresPerMinute);
        options.MaxConcurrentTasks = ReadInt(
            section,
            nameof(RenderNodeServerOptions.MaxConcurrentTasks),
            options.MaxConcurrentTasks);
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static long ReadLong(IConfigurationSection section, string key, long fallback) =>
        long.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
