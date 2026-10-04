using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace NightTales.Api.Internal.Fetcher;

/// <summary>
/// The <c>Fetcher:Internal</c> section (02.5.2-T2, 02.5.1 plan §3.3, §5.2): the second Kestrel listener that serves the
/// fetcher's lease API. The listener binds only when <see cref="Enabled"/>; the secret it is protected with,
/// <c>FETCHER_API_TOKEN</c>, is never part of this section (11.5.2).
/// </summary>
public sealed class FetcherInternalOptions
{
    /// <summary>The configuration section, <c>Fetcher:Internal</c>.</summary>
    public const string SectionName = "Fetcher:Internal";

    /// <summary>The configuration key (environment variable) of the runtime secret.</summary>
    public const string TokenKey = "FETCHER_API_TOKEN";

    /// <summary>The fewest characters of the token; a shorter one is a start-up error.</summary>
    public const int MinTokenLength = 32;

    /// <summary>Whether the internal listener and its routes exist at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>The interface the listener binds: loopback by default, <c>0.0.0.0</c> only in the container environment (11 CR-16).</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>The port of the listener (D10: 8081).</summary>
    public int ListenPort { get; set; } = 8081;

    /// <summary>The request budget of one fetcher instance (<c>workerId</c>) per minute; the next one is 429.</summary>
    public int MaxRequestsPerMinutePerWorker { get; set; } = 60;

    /// <summary>The largest request body the listener reads, in bytes (1 MiB); a larger one is 413.</summary>
    public int MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>The parsed <see cref="ListenAddress"/>; valid only after the options validated.</summary>
    public IPAddress ParsedAddress => IPAddress.Parse(ListenAddress);
}

/// <summary>
/// Fails the start-up on a bad <c>Fetcher:Internal</c> section, and — the safety property of the listener — when it is
/// enabled without a usable <c>FETCHER_API_TOKEN</c>, so the lease API can never run unauthenticated (02.5.2-T2 Config).
/// </summary>
/// <param name="configuration">The host configuration, where the token lives.</param>
public sealed class FetcherInternalOptionsValidator(IConfiguration configuration) : IValidateOptions<FetcherInternalOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FetcherInternalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var problems = new List<string>();
        var token = configuration[FetcherInternalOptions.TokenKey];
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add(
                $"'{FetcherInternalOptions.TokenKey}' is required when '{FetcherInternalOptions.SectionName}:Enabled' is true: "
                + "the fetcher's lease API must never run unauthenticated.");
        }
        else if (token.Length < FetcherInternalOptions.MinTokenLength)
        {
            problems.Add($"'{FetcherInternalOptions.TokenKey}' must be at least {FetcherInternalOptions.MinTokenLength} characters.");
        }

        if (!IPAddress.TryParse(options.ListenAddress, out _))
        {
            problems.Add($"'{FetcherInternalOptions.SectionName}:{nameof(FetcherInternalOptions.ListenAddress)}' must be an IP address.");
        }

        if (options.ListenPort is < 1 or > 65_535)
        {
            problems.Add($"'{FetcherInternalOptions.SectionName}:{nameof(FetcherInternalOptions.ListenPort)}' must be between 1 and 65535.");
        }

        if (options.MaxRequestsPerMinutePerWorker < 1)
        {
            problems.Add($"'{FetcherInternalOptions.SectionName}:{nameof(FetcherInternalOptions.MaxRequestsPerMinutePerWorker)}' must be at least 1.");
        }

        if (options.MaxBodyBytes is < 1_024 or > 16 * 1_024 * 1_024)
        {
            problems.Add($"'{FetcherInternalOptions.SectionName}:{nameof(FetcherInternalOptions.MaxBodyBytes)}' must be between 1 KiB and 16 MiB.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
