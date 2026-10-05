using Microsoft.Extensions.Options;
using NightTales.Fetcher.Core.Fetching;

namespace NightTales.Fetcher;

/// <summary>
/// The start-up rules that hold for the fetcher host only, on top of the shared <see cref="FetcherOptionsValidator"/>
/// (which the Api and the Worker also run, so it keeps the wider platform-side ranges).
/// </summary>
public sealed class FetcherHostOptionsValidator : IValidateOptions<FetcherOptions>
{
    /// <summary>The most tasks one lease call may ask for: the internal API answers 400 above it (02.5.2-T2 frozen hand-off).</summary>
    public const int MaxLeasesPerCall = 4;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FetcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.MaxLeasesPlatformWide is < 1 or > MaxLeasesPerCall)
        {
            problems.Add(
                $"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.MaxLeasesPlatformWide)}' must be between 1 and {MaxLeasesPerCall} for the fetcher host: " +
                "the lease API accepts 'max' 1..4 only, so any other value makes every lease a 400.");
        }

        // The T105 watchdog cancels a fetch after TotalTimeoutSeconds + ExtractTimeoutSeconds; that bound must end before
        // the server's total-lease ceiling, or the Api answers fetch_lease_lost while the fetch is still running.
        if ((long)options.TotalTimeoutSeconds + options.ExtractTimeoutSeconds >= options.MaxLeaseTotalSeconds)
        {
            problems.Add(
                $"'{FetcherOptions.SectionName}:{nameof(FetcherOptions.TotalTimeoutSeconds)}' + '{nameof(FetcherOptions.ExtractTimeoutSeconds)}' " +
                $"must be less than '{FetcherOptions.SectionName}:{nameof(FetcherOptions.MaxLeaseTotalSeconds)}' (the server's lease ceiling).");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
