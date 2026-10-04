using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.Core.Research;
using NightTales.Fetcher.Core.Research.Extraction;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.Fetcher.InternalApi;

namespace NightTales.Fetcher;

/// <summary>
/// The fetcher host's service registrations (02.5.2-T3). <c>Program</c> calls it; the in-process integration tests of the
/// next wave call it too, then swap the <see cref="IAddressValidator"/> and the API client's primary handler (every
/// registration here is a <c>TryAdd</c> or a replaceable handler, 02 D20).
/// </summary>
public static class FetcherHostServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options, the safe source fetcher graph (the production <see cref="GlobalUnicastAddressValidator"/> only),
    /// the internal API client, the metrics and the <see cref="FetcherLoop"/>. It opens no listener and reads no connection string.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration with <c>Fetcher</c>, <c>Fetcher:Internal</c> and <c>FETCHER_API_TOKEN</c>.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddFetcherHost(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var fetcherSection = configuration.GetSection(FetcherOptions.SectionName);
        services.AddOptions<FetcherOptions>().Bind(fetcherSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FetcherOptions>, FetcherOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FetcherOptions>, FetcherHostOptionsValidator>());

        services.AddOptions<FetcherLoopOptions>().Bind(fetcherSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FetcherLoopOptions>, FetcherLoopOptionsValidator>());

        services.AddOptions<FetcherApiOptions>()
            .Bind(configuration.GetSection(FetcherApiOptions.SectionName))
            .PostConfigure(options => options.Token = configuration[FetcherApiOptions.TokenVariable]?.Trim() ?? string.Empty)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FetcherApiOptions>, FetcherApiOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPublicSuffixList, PublicSuffixListProvider>();
        services.TryAddSingleton(provider =>
            new DomainBlocklist(provider.GetRequiredService<IOptions<FetcherOptions>>().Value.BlockedDomains.ToHashSet(StringComparer.Ordinal)));

        // The same graph NightTales.Infrastructure composes for the Api (SourcesServiceCollectionExtensions.AddSourceFetcher),
        // minus the Application-owned Sources options: this host may not reference Infrastructure or Application.
        services.TryAddSingleton<IDnsResolver, SocketDnsResolver>();
        services.TryAddSingleton<IAddressValidator, GlobalUnicastAddressValidator>();
        services.TryAddSingleton(provider => new SafeHttpConnector(
            provider.GetRequiredService<IDnsResolver>(),
            provider.GetRequiredService<IAddressValidator>(),
            provider.GetRequiredService<ILogger<SafeHttpConnector>>()));
        services.TryAddSingleton(provider => SafeHttpClientFactory.Create(provider.GetRequiredService<SafeHttpConnector>()));
        services.TryAddSingleton<IHostRateLimiter, HostRateLimiter>();
        services.TryAddSingleton<RobotsTxtPolicy>();
        services.TryAddSingleton<IRobotsPolicy>(provider => provider.GetRequiredService<RobotsTxtPolicy>());
        services.TryAddSingleton<IArticleExtractor, SmartReaderArticleExtractor>();
        services.TryAddSingleton<ISourceFetcher, SafeHttpSourceFetcher>();

        services.TryAddSingleton(provider =>
            new FetcherMetrics(() => provider.GetRequiredService<RobotsTxtPolicy>().CachedOrigins));
        services.TryAddSingleton<IFetcherExit, HostFetcherExit>();

        // No cookies, no proxy, no redirects, 15 s per request; the handler is never rotated out from under the loop's
        // single client (the connection lifetime does the DNS refresh).
        services.AddHttpClient<IFetcherApi, FetcherApiClient>((provider, client) =>
            {
                var api = provider.GetRequiredService<IOptions<FetcherApiOptions>>().Value;
                client.BaseAddress = new Uri(api.BaseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(FetcherApiOptions.RequestTimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddHostedService<FetcherLoop>();

        return services;
    }
}
