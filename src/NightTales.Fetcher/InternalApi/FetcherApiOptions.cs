using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace NightTales.Fetcher.InternalApi;

/// <summary>
/// The fetcher's view of the Api's internal listener (02.5.2-T3): the base URL from <c>Fetcher:Internal:BaseUrl</c> and
/// the bearer token from the <c>FETCHER_API_TOKEN</c> environment variable. The token is never bound from a file, never
/// logged and never part of a URL or exception message.
/// </summary>
public sealed class FetcherApiOptions
{
    /// <summary>The configuration section, <c>Fetcher:Internal</c>.</summary>
    public const string SectionName = "Fetcher:Internal";

    /// <summary>The environment variable (and configuration key) that carries the token (11.5.2).</summary>
    public const string TokenVariable = "FETCHER_API_TOKEN";

    /// <summary>The per-request timeout of the internal API client, in seconds.</summary>
    public const int RequestTimeoutSeconds = 15;

    /// <summary>The Api's internal base URL, absolute http or https.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The bearer token; set from <see cref="TokenVariable"/> only.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The worker id sent with every call (1-64 of <c>[A-Za-z0-9._:-]</c>); empty derives one from the machine and process.</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>The worker id in force: the configured one or <c>fetcher-{machine}-{pid}</c>, reduced to the allowed alphabet.</summary>
    public string EffectiveWorkerId => ResolveWorkerId(WorkerId);

    internal static string ResolveWorkerId(string? configured)
    {
        var raw = string.IsNullOrWhiteSpace(configured)
            ? string.Create(CultureInfo.InvariantCulture, $"fetcher-{Environment.MachineName}-{Environment.ProcessId}")
            : configured;
        var builder = new StringBuilder(Math.Min(raw.Length, 64));
        foreach (var character in raw)
        {
            if (builder.Length == 64)
            {
                break;
            }

            builder.Append(IsAllowed(character) ? character : '-');
        }

        return builder.Length == 0 ? "fetcher" : builder.ToString();
    }

    internal static bool IsAllowed(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-';
}

/// <summary>Fails the start-up when the base URL is missing or not an absolute http(s) URL, or the token is empty.</summary>
public sealed class FetcherApiOptionsValidator : IValidateOptions<FetcherApiOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FetcherApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            problems.Add($"'{FetcherApiOptions.SectionName}:BaseUrl' is required.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            problems.Add($"'{FetcherApiOptions.SectionName}:BaseUrl' must be an absolute http or https URL without credentials.");
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            problems.Add($"{FetcherApiOptions.TokenVariable} is required.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
