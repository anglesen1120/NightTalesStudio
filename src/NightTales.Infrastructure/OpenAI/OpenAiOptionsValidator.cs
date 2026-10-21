using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Text;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// Fails the start-up on an unusable <c>OpenAi</c> section (02.2.1-T2 step 1): a missing key, a base URL
/// other than the pinned relay outside Development, or a step without a model, effort or timeout.
/// </summary>
/// <param name="environment">Decides whether a non-pinned base URL is allowed (the e2e stub, Development only).</param>
public sealed class OpenAiOptionsValidator(IHostEnvironment environment) : IValidateOptions<OpenAiOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OpenAiOptions options)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            problems.Add($"'{OpenAiOptions.SectionName}:{nameof(OpenAiOptions.ApiKey)}' is required (set OpenAi__ApiKey from the secret store).");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http"))
        {
            problems.Add($"'{OpenAiOptions.SectionName}:{nameof(OpenAiOptions.BaseUrl)}' must be an absolute http(s) URL.");
        }
        else if (!environment.IsDevelopment()
            && !string.Equals(baseUrl.AbsoluteUri.TrimEnd('/'), OpenAiOptions.PinnedBaseUrl, StringComparison.Ordinal))
        {
            problems.Add($"'{OpenAiOptions.SectionName}:{nameof(OpenAiOptions.BaseUrl)}' must be {OpenAiOptions.PinnedBaseUrl} outside Development.");
        }
        else if (environment.IsDevelopment()
            && baseUrl.Scheme == "http"
            && !baseUrl.IsLoopback
            && !string.Equals(baseUrl.AbsoluteUri.TrimEnd('/'), OpenAiOptions.PinnedBaseUrl, StringComparison.Ordinal))
        {
            // The key and the prompts would travel in clear text: plain http is for the local e2e stub only.
            problems.Add($"'{OpenAiOptions.SectionName}:{nameof(OpenAiOptions.BaseUrl)}' must be https or a loopback host in Development.");
        }

        foreach (var step in Enum.GetValues<TextStep>())
        {
            if (!options.Models.TryGetValue(step, out var model) || string.IsNullOrWhiteSpace(model))
            {
                problems.Add($"'{OpenAiOptions.SectionName}:Models:{step}' is required.");
            }

            if (!options.Effort.TryGetValue(step, out var effort) || !OpenAiOptions.AllowedEfforts.Contains(effort))
            {
                problems.Add($"'{OpenAiOptions.SectionName}:Effort:{step}' must be one of {string.Join(", ", OpenAiOptions.AllowedEfforts.Order())}.");
            }

            if (!options.RequestTimeout.TryGetValue(step, out var timeout) || timeout <= TimeSpan.Zero)
            {
                problems.Add($"'{OpenAiOptions.SectionName}:RequestTimeout:{step}' must be a positive duration.");
            }
        }

        if (options.RetryBaseDelay < TimeSpan.Zero)
        {
            problems.Add($"'{OpenAiOptions.SectionName}:{nameof(OpenAiOptions.RetryBaseDelay)}' cannot be negative.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
