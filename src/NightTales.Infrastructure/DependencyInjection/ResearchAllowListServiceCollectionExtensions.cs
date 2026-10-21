using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Infrastructure.Research;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>Registers the research allow-list (02.7.2-T1).</summary>
public static class ResearchAllowListServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IResearchAllowList"/> as a singleton loaded from the embedded file, and a start-up check that
    /// loads it so a broken file fails the boot. Call after <c>AddSources</c>, which registers the public-suffix list and
    /// the blocklist this depends on.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddResearchAllowList(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IResearchAllowList>(provider => EmbeddedResearchAllowList.LoadEmbedded(
            provider.GetRequiredService<NightTales.Domain.Research.IPublicSuffixList>(),
            provider.GetRequiredService<NightTales.Domain.Research.DomainBlocklist>()));
        services.AddHostedService<ResearchAllowListStartupValidator>();

        return services;
    }
}
