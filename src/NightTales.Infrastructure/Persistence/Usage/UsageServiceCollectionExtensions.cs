using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Usage;
using NightTales.Infrastructure.Persistence.Queries;
using NightTales.Infrastructure.Persistence.Seeding;

namespace NightTales.Infrastructure.Persistence.Usage;

/// <summary>Composition of the price versions, the cost calculator inputs and the usage meter (02.2.4-T1).</summary>
public static class UsageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PricingOptions"/> (<c>OpenAi:Pricing</c>), the <see cref="PriceVersionSeeder"/>
    /// (an <c>ISeeder</c>, run by the 09.2.5-T1 <c>SeedRunner</c>: Development auto-seed and <c>nts-seed</c>),
    /// <see cref="IPriceCatalog"/>, <see cref="IUsageMeter"/> and
    /// <see cref="IStoryCostEstimator"/>. Needs <c>AddPersistence</c> and <c>AddPromptCatalog</c> in the same host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddUsageMetering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PricingOptions>()
            .BindConfiguration(PricingOptions.SectionName)
            .Validate(
                options => options.SurchargeThresholdTokens > 0,
                $"'{PricingOptions.SectionName}:{nameof(PricingOptions.SurchargeThresholdTokens)}' must be greater than zero.")
            .ValidateOnStart();

        services.AddScoped<ISeeder, PriceVersionSeeder>();
        services.TryAddSingleton<IPriceCatalog, PriceCatalog>();
        services.TryAddSingleton<IUsageMeter, UsageMeter>();
        services.TryAddSingleton<IStoryCostEstimator, StoryCostEstimator>();

        return services;
    }
}
