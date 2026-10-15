using System.Globalization;
using Microsoft.Extensions.Options;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// Validates the <c>Render</c> section's client half at start-up (09 README BD6/W9, the same shape as
/// <c>BibleOptionsValidator</c>): every timeout, cap and breaker number must be usable, so an impossible
/// configuration fails the boot instead of the first render.
/// </summary>
/// <remarks>
/// <para>
/// A zero or negative timeout would abort every call before it is made, a zero cap would refuse every
/// output, and a zero sampling window or throughput would leave the circuit breaker undefined; all of them
/// are rejected here. <see cref="RenderClientOptions.ClientOptions.MaxRetries"/> is the one exception: zero
/// is a legal, deliberate "do not retry", so only a negative value is refused.
/// </para>
/// <para>
/// Public, like the other validators in this solution, because the solution has no
/// <c>InternalsVisibleTo</c> and the start-up contract is asserted by a unit test.
/// </para>
/// </remarks>
public sealed class RenderClientOptionsValidator : IValidateOptions<RenderClientOptions>
{
    /// <summary>The largest <c>FailureRatio</c> a breaker can be configured with: a ratio is a fraction.</summary>
    public const double MaxFailureRatio = 1.0;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RenderClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var client = options.Client;
        var breaker = client.CircuitBreaker;
        var failures = new List<string>();

        RequirePositive(failures, client.AttemptSeconds, "Client:AttemptSeconds");
        RequirePositive(failures, client.TotalSeconds, "Client:TotalSeconds");
        RequireNotNegative(failures, client.MaxRetries, "Client:MaxRetries");
        RequirePositive(failures, breaker.MinimumThroughput, "Client:CircuitBreaker:MinimumThroughput");
        RequireRatio(failures, breaker.FailureRatio, "Client:CircuitBreaker:FailureRatio");
        RequirePositive(failures, breaker.SamplingDurationSeconds, "Client:CircuitBreaker:SamplingDurationSeconds");
        RequirePositive(failures, breaker.BreakDurationSeconds, "Client:CircuitBreaker:BreakDurationSeconds");
        RequirePositive(failures, options.Download.IdleSeconds, "Download:IdleSeconds");
        RequirePositive(failures, options.DownloadMaxMinutes, nameof(RenderClientOptions.DownloadMaxMinutes));
        RequirePositive(failures, options.MaxVideoBytes, nameof(RenderClientOptions.MaxVideoBytes));
        RequirePositive(failures, options.MaxSubtitleBytes, nameof(RenderClientOptions.MaxSubtitleBytes));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Records a failure when a timeout, cap or breaker number is zero or negative.</summary>
    private static void RequirePositive(List<string> failures, long value, string key)
    {
        if (value <= 0)
        {
            failures.Add($"'{RenderClientOptions.SectionName}:{key}' must be greater than zero.");
        }
    }

    /// <summary>Records a failure when a count is negative (zero disables the behaviour, which is allowed).</summary>
    private static void RequireNotNegative(List<string> failures, int value, string key)
    {
        if (value < 0)
        {
            failures.Add($"'{RenderClientOptions.SectionName}:{key}' cannot be negative.");
        }
    }

    /// <summary>
    /// Records a failure when a failure ratio is not inside <c>(0, 1]</c>: neither a breaker that can never
    /// see enough failures nor one that opens on a single success is a usable circuit breaker.
    /// </summary>
    private static void RequireRatio(List<string> failures, double value, string key)
    {
        if (value <= 0 || value > MaxFailureRatio)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{RenderClientOptions.SectionName}:{key}' must be greater than zero and at most {MaxFailureRatio}."));
        }
    }
}
