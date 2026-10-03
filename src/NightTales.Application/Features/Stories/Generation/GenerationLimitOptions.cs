namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// The <c>Story</c> section of the FR-02-7 limit (02.4.2-T1): how many user-triggered story generations one
/// episode may queue inside a rolling window. Both keys have defaults, so no host needs a placeholder.
/// </summary>
public sealed class GenerationLimitOptions
{
    /// <summary>The configuration section, <c>Story</c>.</summary>
    public const string SectionName = "Story";

    /// <summary>FR-02-7's number: counted runs per episode inside the window.</summary>
    public const int DefaultDailyGenerationLimit = 5;

    /// <summary>The window length in hours (F06: "which day?" is a rolling 24 h).</summary>
    public const int DefaultLimitWindowHours = 24;

    /// <summary>The largest accepted <c>DailyGenerationLimit</c>; a typo above it fails the start-up.</summary>
    public const int MaxDailyGenerationLimit = 1000;

    /// <summary>The largest accepted <c>LimitWindowHours</c>: 366 days.</summary>
    public const int MaxLimitWindowHours = 24 * 366;

    /// <summary>The most counted runs one episode may have inside the window; the next one is rejected.</summary>
    public int DailyGenerationLimit { get; set; } = DefaultDailyGenerationLimit;

    /// <summary>The length of the rolling window, in hours.</summary>
    public int LimitWindowHours { get; set; } = DefaultLimitWindowHours;

    /// <summary>The start-up problems of <paramref name="options"/>; empty when it is valid.</summary>
    /// <param name="options">The bound section.</param>
    public static IReadOnlyList<string> Validate(GenerationLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (options.DailyGenerationLimit < 1 || options.DailyGenerationLimit > MaxDailyGenerationLimit)
        {
            problems.Add($"'{SectionName}:{nameof(DailyGenerationLimit)}' must be between 1 and {MaxDailyGenerationLimit}.");
        }

        if (options.LimitWindowHours < 1 || options.LimitWindowHours > MaxLimitWindowHours)
        {
            problems.Add($"'{SectionName}:{nameof(LimitWindowHours)}' must be between 1 and {MaxLimitWindowHours}.");
        }

        return problems;
    }
}
