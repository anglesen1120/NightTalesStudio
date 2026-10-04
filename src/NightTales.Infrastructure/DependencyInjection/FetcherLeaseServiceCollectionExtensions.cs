using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Research;
using NightTales.Infrastructure.Storage;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// The Api side of the fetcher's internal lease API (02.5.2-T2): the queue-claim store, the extracted-text store, the
/// result caps and the <c>fetch_leases_active</c> gauge. Only the Api composes it; the Worker's sweeper has its own store.
/// </summary>
public static class FetcherLeaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the lease API's services. It needs <see cref="SourcesServiceCollectionExtensions.AddSources"/> (the
    /// validated <see cref="FetcherOptions"/>, the blocklist and the public-suffix list), <c>AddStorage</c> (the blob
    /// store) and the telemetry registration (the metrics) in the same host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddFetcherLeaseApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IFetchTaskLeaseStore, FetchTaskLeaseStore>();
        services.TryAddSingleton<IExtractedTextStore, ExtractedTextStore>();
        services.TryAddSingleton(provider =>
            new FetchResultLimits(provider.GetRequiredService<IOptions<FetcherOptions>>().Value.MaxTextChars));

        return services;
    }
}
