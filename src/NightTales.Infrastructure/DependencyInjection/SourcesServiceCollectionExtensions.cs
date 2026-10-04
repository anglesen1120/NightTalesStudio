using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Features.Research.Sources;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research;
using NightTales.Infrastructure.Research.Extraction;
using NightTales.Infrastructure.Research.Http;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The story-source rules (02.5.1-T1): the public-suffix list, the domain blocklist and the volume caps. Only the Api
/// composes it — the add, remove, retry and list commands are Api requests; the fetcher host validates its own keys.
/// </summary>
public static class SourcesServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <c>Sources</c> and <c>Fetcher:BlockedDomains</c> (both fail the boot, not the request) and
    /// registers <see cref="IPublicSuffixList"/>, <see cref="DomainBlocklist"/> and the plain
    /// <see cref="SourcesOptions"/> the Application handlers take.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddSources(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IPublicSuffixList, PublicSuffixListProvider>();

        services.AddOptions<SourcesOptions>().Bind(configuration.GetSection(SourcesOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SourcesOptions>, SourcesOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<SourcesOptions>>().Value);

        services.AddOptions<FetcherOptions>().Bind(configuration.GetSection(FetcherOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FetcherOptions>, FetcherOptionsValidator>());
        services.TryAddSingleton(provider =>
            new DomainBlocklist(provider.GetRequiredService<IOptions<FetcherOptions>>().Value.BlockedDomains.ToHashSet(StringComparer.Ordinal)));

        return services;
    }

    /// <summary>
    /// Registers the safe source fetcher (02.5.2-T1): the one <see cref="HttpClient"/> whose connect callback resolves,
    /// validates and connects, the robots policy, the rate limiter, the extractor and <see cref="ISourceFetcher"/>.
    /// Only the production <see cref="IAddressValidator"/> (<see cref="GlobalUnicastAddressValidator"/>) is registered; a
    /// test host that registers its own validator before this call wins (<c>TryAdd</c>), and no production path ever
    /// registers a loopback-allowing one (02 D20). It also registers <see cref="AddSources"/>' options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration with the <c>Fetcher</c> section.</param>
    public static IServiceCollection AddSourceFetcher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSources(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SocketDnsResolver>();
        services.TryAddSingleton<IAddressValidator, GlobalUnicastAddressValidator>();
        services.TryAddSingleton<SafeHttpConnector>(provider => new SafeHttpConnector(
            provider.GetRequiredService<IDnsResolver>(),
            provider.GetRequiredService<IAddressValidator>(),
            provider.GetRequiredService<ILogger<SafeHttpConnector>>()));
        services.TryAddSingleton(provider => SafeHttpClientFactory.Create(provider.GetRequiredService<SafeHttpConnector>()));
        services.TryAddSingleton<IHostRateLimiter, HostRateLimiter>();
        services.TryAddSingleton<IRobotsPolicy, RobotsTxtPolicy>();
        services.TryAddSingleton<IArticleExtractor, SmartReaderArticleExtractor>();
        services.TryAddSingleton<ISourceFetcher, SafeHttpSourceFetcher>();

        return services;
    }
}
