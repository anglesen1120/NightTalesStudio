using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Infrastructure.Prompts;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the 02 prompt catalog (02.2.3-T1, 02 README D5): the embedded resource source, the
/// singleton catalog and the start-up gate that loads it.
/// </summary>
/// <remarks>
/// Called by both composition roots — the Worker's <c>AddWorkerServices</c> and the Api's
/// <c>Program</c> — because both hosts write stories and both must refuse to start with a catalog that
/// does not match its manifest. The catalog is a singleton because it is read-only data loaded once;
/// the hosted service is what forces that load during start-up.
/// </remarks>
public static class PromptCatalogServiceCollectionExtensions
{
    /// <summary>Adds the prompt catalog and its start-up gate to <paramref name="services"/>.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddPromptCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPromptResourceSource, EmbeddedPromptResourceSource>();
        services.AddSingleton<IPromptCatalog, PromptCatalog>();
        services.AddSingleton<IHostedService, PromptCatalogStartupValidator>();

        return services;
    }
}
