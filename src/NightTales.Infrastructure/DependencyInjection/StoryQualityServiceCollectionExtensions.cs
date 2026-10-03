using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>The story quality checker (02.3.4-T1).</summary>
public static class StoryQualityServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="StoryQualityOptions"/> from <c>Story:Quality</c> (every key is required: a
    /// missing or invalid block fails the start-up) and registers <see cref="StoryQualityChecker"/> as a singleton
    /// built from the validated thresholds. The checker is pure and thread-safe.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddStoryQuality(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<StoryQualityOptions>()
            .BindConfiguration(StoryQualityOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<StoryQualityOptions>, StoryQualityOptionsValidation>());
        services.TryAddSingleton(provider =>
            new StoryQualityChecker(provider.GetRequiredService<IOptions<StoryQualityOptions>>().Value.ToThresholds()));

        return services;
    }
}

/// <summary>Fails the start-up on a missing or invalid <c>Story:Quality</c> block, with every problem listed.</summary>
internal sealed class StoryQualityOptionsValidation : IValidateOptions<StoryQualityOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StoryQualityOptions options)
    {
        var problems = StoryQualityOptionsValidator.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
