namespace NightTales.Application.Features.Research.Jobs;

/// <summary>
/// The Worker's view of the <c>Fetcher</c> section (02.5.2-T2 plan, Config): only the keys the sweeper reads. The Api binds
/// the same section into its own options class, so neither host depends on the other's type.
/// </summary>
public sealed class FetchLeaseSweeperOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Fetcher";

    /// <summary>A task whose lease expires with this many attempts used fails with <c>source_unreachable</c> (D10).</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>How long a requeued task waits before it can be leased again.</summary>
    public int RequeueBackoffSeconds { get; set; } = 30;

    /// <summary>Documents the contract: the recurring cron is fixed at every minute; the key is validated but not read.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>The validation messages of <paramref name="options"/>; empty when it is valid.</summary>
    /// <param name="options">The bound options.</param>
    /// <returns>One message per invalid key.</returns>
    public static IReadOnlyList<string> Validate(FetchLeaseSweeperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        if (options.MaxAttempts < 1)
        {
            errors.Add($"'{SectionName}:{nameof(MaxAttempts)}' must be at least 1.");
        }

        if (options.RequeueBackoffSeconds < 1)
        {
            errors.Add($"'{SectionName}:{nameof(RequeueBackoffSeconds)}' must be at least 1.");
        }

        if (options.SweepIntervalSeconds < 1)
        {
            errors.Add($"'{SectionName}:{nameof(SweepIntervalSeconds)}' must be at least 1.");
        }

        return errors;
    }
}
