using Microsoft.Extensions.Options;
using NightTales.Application.Features.Research.Sources;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research;

/// <summary>
/// Fails the start-up on a bad <c>Fetcher:BlockedDomains</c> (02.5.1 T1 Config): the list must be non-empty, and every
/// entry a lowercase registrable domain — no scheme, no path, no wildcard, no IP literal, no whitespace.
/// </summary>
/// <param name="psl">The public-suffix list that decides what a registrable domain is.</param>
public sealed class FetcherOptionsValidator(IPublicSuffixList psl) : IValidateOptions<FetcherOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FetcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = $"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.BlockedDomains)}'";
        if (options.BlockedDomains.Count == 0)
        {
            return ValidateOptionsResult.Fail($"{key} must not be empty.");
        }

        var problems = new List<string>();
        for (var index = 0; index < options.BlockedDomains.Count; index++)
        {
            var entry = options.BlockedDomains[index];
            if (!IsRegistrableDomain(entry))
            {
                problems.Add($"{key}[{index}] must be a lowercase registrable domain (no scheme, path, wildcard or IP literal).");
            }
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private bool IsRegistrableDomain(string? entry) =>
        !string.IsNullOrEmpty(entry)
        && entry.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')
        && entry.Contains('.', StringComparison.Ordinal)
        && !entry.StartsWith('.')
        && !entry.EndsWith('.')
        && !entry.Contains("..", StringComparison.Ordinal)
        && !entry.Split('.').All(label => label.All(char.IsAsciiDigit))
        && psl.GetRegistrableDomain(entry) == entry;
}

/// <summary>Fails the start-up on a cap of 0 or a <c>MaxNoteChars</c> outside [1, 10,000] (02.5.1 T1 Config).</summary>
public sealed class SourcesOptionsValidator : IValidateOptions<SourcesOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SourcesOptions options)
    {
        var problems = SourcesOptions.Validate(options);

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
