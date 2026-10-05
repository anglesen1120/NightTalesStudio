using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Domain.Stories.Similarity;
using NightTales.Infrastructure.Persistence.Queries;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>Registers the similarity corpus and startup-validated thresholds for Api and Worker.</summary>
public static class SimilarityServiceCollectionExtensions
{
    /// <summary>Binds the optional Similarity block; invalid bounds fail the host startup.</summary>
    public static IServiceCollection AddSimilarity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SimilaritySettings>().BindConfiguration("Similarity").ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SimilaritySettings>, SimilaritySettingsValidator>());
        // Application receives the plain validated settings; IOptions stays in the composition root.
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<SimilaritySettings>>().Value);
        services.TryAddScoped<ISimilarityCorpusProvider, SimilarityCorpusProvider>();
        return services;
    }
}
