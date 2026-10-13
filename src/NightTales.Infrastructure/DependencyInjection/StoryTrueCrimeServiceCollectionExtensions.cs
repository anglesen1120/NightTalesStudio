using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.DependencyInjection;

/// <summary>The true-crime approval and attestation options (02.3.3-T1).</summary>
public static class StoryTrueCrimeServiceCollectionExtensions
{
    /// <summary>
    /// Binds and validates <see cref="StoryTrueCrimeOptions"/> from <c>Story:TrueCrime</c>. Both keys
    /// carry a default, so the block is optional and no deployment is broken by its absence; a value
    /// outside the bounds, or an attestation version the prompt catalog does not ship, fails the
    /// start-up instead of the first approval. The plain validated value is registered as a singleton
    /// for the Application gate and the story read model, while <c>IOptions</c> stays in this
    /// composition root — the pattern of <c>AddSimilarity</c>.
    /// </summary>
    /// <remarks>
    /// Needs <c>AddPromptCatalog</c> in the same host: the version cross-check of
    /// <see cref="StoryTrueCrimeAttestationVersionValidation"/> reads the shipped manifest through
    /// <see cref="IPromptCatalog"/>. Both hosts that write stories register it (02 README D5/F22).
    /// </remarks>
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
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<StoryTrueCrimeOptions>, StoryTrueCrimeAttestationVersionValidation>());
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

/// <summary>
/// Fails the start-up when <c>Story:TrueCrime:AttestationVersion</c> names a version the prompt catalog
/// does not ship for some content language (02.3.3-T1, 02 README T29).
/// </summary>
/// <remarks>
/// The tick the owner gives is a statement about wording they read, so a host whose configured version
/// the catalog does not serve would show no clause text while still accepting ticks for wording that was
/// never displayed. The check runs for every <see cref="Language"/> and reports one problem per
/// language, naming the key and the language and never echoing a value or a clause text (the
/// <see cref="StoryTrueCrimeOptionsValidator"/> convention). A file the manifest cannot resolve already
/// fails the boot earlier, in the catalog's own load.
/// </remarks>
/// <param name="catalog">The validated catalog of the host; both hosts register it through <c>AddPromptCatalog</c>.</param>
internal sealed class StoryTrueCrimeAttestationVersionValidation(IPromptCatalog catalog)
    : IValidateOptions<StoryTrueCrimeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StoryTrueCrimeOptions options)
    {
        var problems = new List<string>();

        foreach (var language in Enum.GetValues<Language>())
        {
            if (!string.Equals(catalog.GetAttestation(language).Version, options.AttestationVersion, StringComparison.Ordinal))
            {
                problems.Add(
                    $"'{StoryTrueCrimeOptions.SectionName}:{nameof(StoryTrueCrimeOptions.AttestationVersion)}' must name the "
                    + $"attestation version the shipped prompt catalog declares for language '{language}', and it does not.");
            }
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
