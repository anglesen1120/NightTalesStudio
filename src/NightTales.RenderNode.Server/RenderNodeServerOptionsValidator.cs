using System.Text;
using Microsoft.Extensions.Options;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Validates a bound <see cref="RenderNodeServerOptions"/> (05 D15 S3, §10 T68). The adapter host calls it
/// at startup and exits with a usage error on failure; registered as an <c>IValidateOptions</c> it also makes
/// <c>IOptions&lt;…&gt;.Value</c> throw instead of serving with an unusable key.
/// </summary>
/// <remarks>
/// The failure text names the setting and never its value, so a bad key can never reach a log or a bundle.
/// </remarks>
public sealed class RenderNodeServerOptionsValidator : IValidateOptions<RenderNodeServerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RenderNodeServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var apiKeyBytes = Encoding.UTF8.GetByteCount(options.ApiKey);
        if (apiKeyBytes < RenderNodeServerOptions.MinimumApiKeyBytes)
        {
            failures.Add(
                $"{RenderNodeServerOptions.SectionName}:ApiKey must be at least "
                + $"{RenderNodeServerOptions.MinimumApiKeyBytes} bytes (05 D15 S3).");
        }

        // An empty or unset ApiKeyNext disables the rotation window; it is never an empty-string match.
        if (!string.IsNullOrEmpty(options.ApiKeyNext))
        {
            if (Encoding.UTF8.GetByteCount(options.ApiKeyNext) < RenderNodeServerOptions.MinimumApiKeyBytes)
            {
                failures.Add(
                    $"{RenderNodeServerOptions.SectionName}:ApiKeyNext must be unset or at least "
                    + $"{RenderNodeServerOptions.MinimumApiKeyBytes} bytes (05 D15 S3).");
            }

            if (string.Equals(options.ApiKeyNext, options.ApiKey, StringComparison.Ordinal))
            {
                failures.Add($"{RenderNodeServerOptions.SectionName}:ApiKeyNext must differ from ApiKey (05 D15 S3).");
            }
        }

        if (options.MaxBodyBytes <= 0)
        {
            failures.Add($"{RenderNodeServerOptions.SectionName}:MaxBodyBytes must be positive.");
        }

        if (options.AuthFailuresPerMinute < 0)
        {
            failures.Add($"{RenderNodeServerOptions.SectionName}:AuthFailuresPerMinute must not be negative.");
        }

        if (options.MaxConcurrentTasks < 1)
        {
            failures.Add($"{RenderNodeServerOptions.SectionName}:MaxConcurrentTasks must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
