using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Rendering;
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
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PricingOptions>, PricingOptionsValidator>());

        services.AddScoped<ISeeder, PriceVersionSeeder>();
        services.TryAddSingleton<IPriceCatalog, PriceCatalog>();
        services.TryAddSingleton<IUsageMeter, UsageMeter>();
        services.TryAddSingleton<IStoryCostEstimator, StoryCostEstimator>();

        // 03.2.1-T1: the Images section (scene budget, D3) and the worst-case image bound (D8).
        services.AddOptions<ImagesOptions>()
            .BindConfiguration(ImagesOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ImagesOptions>, ImagesOptionsValidation>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<ImagesOptions>>().Value);
        services.TryAddSingleton<IImageCostEstimator, ImageCostEstimator>();

        return services;
    }
}

/// <summary>Fails the start-up on an invalid <c>OpenAi:Pricing</c> section (rates, units, threshold), with every problem listed.</summary>
internal sealed class PricingOptionsValidator : IValidateOptions<PricingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PricingOptions options)
    {
        var problems = PricingOptions.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{PricingOptions.SectionName}: {problem}"));
    }
}

/// <summary>Fails the start-up on an invalid <c>Images</c> section, with every problem listed.</summary>
internal sealed class ImagesOptionsValidation : IValidateOptions<ImagesOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ImagesOptions options)
    {
        var problems = ImagesOptionsValidator.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{ImagesOptions.SectionName}: {problem}"));
    }
}
