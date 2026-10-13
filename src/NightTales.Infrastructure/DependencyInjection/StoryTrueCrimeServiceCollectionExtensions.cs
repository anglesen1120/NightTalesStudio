using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Stories.Approval;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>The true-crime approval and attestation options (02.3.3-T1).</summary>
public static class StoryTrueCrimeServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="StoryTrueCrimeOptions"/> from <c>Story:TrueCrime</c>. Both keys
    /// carry a default, so the block is optional and no deployment is broken by its absence; a value
    /// outside the bounds fails the start-up instead of the first approval. The plain validated value is
    /// registered as a singleton for the Application gate and the story read model, while
    /// <c>IOptions</c> stays in this composition root — the pattern of <c>AddSimilarity</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddStoryTrueCrime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<StoryTrueCrimeOptions>()
            .BindConfiguration(StoryTrueCrimeOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<StoryTrueCrimeOptions>, StoryTrueCrimeOptionsValidation>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<StoryTrueCrimeOptions>>().Value);

        return services;
    }
}

/// <summary>Fails the start-up on an invalid <c>Story:TrueCrime</c> block, with every problem listed.</summary>
internal sealed class StoryTrueCrimeOptionsValidation : IValidateOptions<StoryTrueCrimeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StoryTrueCrimeOptions options)
    {
        var problems = StoryTrueCrimeOptionsValidator.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
