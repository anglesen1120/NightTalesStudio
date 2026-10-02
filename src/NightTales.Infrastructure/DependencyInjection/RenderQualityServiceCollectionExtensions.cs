using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Rendering.Quality;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The <c>RenderQuality</c> section of the verdict's policy (05 README D10, 05.5.1-T2 step 5): bound
/// from configuration and validated on start. The Worker's composition root calls it, because the job
/// that evaluates the policy (09.6.3-T2's ingest) runs there.
/// </summary>
public static class RenderQualityServiceCollectionExtensions
{
    /// <summary>Binds and validates <see cref="RenderQualityPolicy"/> from <c>RenderQuality</c>.</summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <param name="configuration">The host configuration carrying the <c>RenderQuality</c> section.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRenderQuality(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // `Configure(section)` binds from the configuration instance the host already has instead of
        // resolving an `IConfiguration` service at options-resolution time, so the registration also
        // works in a bare container (the unit tests build one) and does not depend on the host having
        // registered its configuration as a service.
        services.Configure<RenderQualityPolicy>(configuration.GetSection(RenderQualityPolicy.SectionName));
        services.AddOptions<RenderQualityPolicy>().ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RenderQualityPolicy>, RenderQualityPolicyValidator>());

        return services;
    }
}
