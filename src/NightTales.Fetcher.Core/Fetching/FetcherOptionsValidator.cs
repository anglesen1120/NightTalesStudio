using Microsoft.Extensions.Options;
using NightTales.Domain.Research;

namespace NightTales.Fetcher.Core.Fetching;

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

        RequirePositive(problems, nameof(FetcherOptions.TotalTimeoutSeconds), options.TotalTimeoutSeconds);
        RequirePositive(problems, nameof(FetcherOptions.RobotsTimeoutSeconds), options.RobotsTimeoutSeconds);
        RequirePositive(problems, nameof(FetcherOptions.RobotsCacheHours), options.RobotsCacheHours);
        RequirePositive(problems, nameof(FetcherOptions.CrawlDelayMaxSeconds), options.CrawlDelayMaxSeconds);
        RequirePositive(problems, nameof(FetcherOptions.MaxResponseBytes), options.MaxResponseBytes);
        RequirePositive(problems, nameof(FetcherOptions.MaxRobotsBytes), options.MaxRobotsBytes);
        RequirePositive(problems, nameof(FetcherOptions.MinTextChars), options.MinTextChars);
        RequirePositive(problems, nameof(FetcherOptions.MaxTextChars), options.MaxTextChars);
        RequirePositive(problems, nameof(FetcherOptions.ExtractTimeoutSeconds), options.ExtractTimeoutSeconds);
        RequirePositive(problems, nameof(FetcherOptions.ExtractMaxElements), options.ExtractMaxElements);
        RequirePositive(problems, nameof(FetcherOptions.ExtractMaxDepth), options.ExtractMaxDepth);
        if (options.MaxRedirects is < 0 or > 10)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.MaxRedirects)}' must be between 0 and 10.");
        }

        if (options.MinTextChars > options.MaxTextChars)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.MinTextChars)}' must not exceed MaxTextChars.");
        }

        if (options.LeaseSeconds is < 10 or > 3_600)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.LeaseSeconds)}' must be between 10 and 3600.");
        }

        if (options.HeartbeatSeconds < 1 || options.HeartbeatSeconds >= options.LeaseSeconds)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.HeartbeatSeconds)}' must be at least 1 and less than LeaseSeconds.");
        }

        if (options.MaxLeaseTotalSeconds <= options.LeaseSeconds || options.MaxLeaseTotalSeconds > 86_400)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.MaxLeaseTotalSeconds)}' must be greater than LeaseSeconds and at most 86400.");
        }

        if (options.MaxLeasesPlatformWide is < 1 or > 64)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.MaxLeasesPlatformWide)}' must be between 1 and 64.");
        }

        if (options.MaxLeasesPerDomain != 1)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.MaxLeasesPerDomain)}' must be 1: the lease statement leases one task per registrable domain.");
        }

        if (string.IsNullOrWhiteSpace(options.PublicHost) || options.PublicHost.Contains('/', StringComparison.Ordinal)
            || options.PublicHost.Contains(':', StringComparison.Ordinal) || options.PublicHost.Any(char.IsWhiteSpace))
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.PublicHost)}' must be a bare host name (no scheme, path or port).");
        }

        if (string.IsNullOrWhiteSpace(options.AcceptLanguage))
        {
            problems.Add($"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.AcceptLanguage)}' must not be empty.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private static void RequirePositive(List<string> problems, string name, int value)
    {
        if (value < 1)
        {
            problems.Add($"'{FetcherOptions.SectionName}:{name}' must be at least 1.");
        }
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
