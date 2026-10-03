namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The <c>Budget</c> section (02 README D1): the platform spend caps the <see cref="IBudgetGuard"/> enforces.
/// Every key has a default, so no host needs a placeholder. Plain settings: the binding lives in Infrastructure,
/// because Application references no options package.
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>The configuration section, <c>Budget</c>.</summary>
    public const string SectionName = "Budget";

    /// <summary>The default daily cap in USD.</summary>
    public const decimal DefaultMaxPlatformCostUsdPerDay = 3m;

    /// <summary>The default monthly cap in USD.</summary>
    public const decimal DefaultMaxPlatformCostUsdPerMonth = 40m;

    /// <summary>Test-only kill-switch; false makes the guard allow everything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most the platform may spend (spent + open bounds + the new bound) per UTC day.</summary>
    public decimal MaxPlatformCostUsdPerDay { get; set; } = DefaultMaxPlatformCostUsdPerDay;

    /// <summary>The most the platform may spend per UTC calendar month.</summary>
    public decimal MaxPlatformCostUsdPerMonth { get; set; } = DefaultMaxPlatformCostUsdPerMonth;

    /// <summary>The start-up problems of <paramref name="options"/>; empty when it is valid.</summary>
    /// <param name="options">The bound section.</param>
    public static IReadOnlyList<string> Validate(BudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (options.MaxPlatformCostUsdPerDay <= 0m)
        {
            problems.Add($"'{SectionName}:{nameof(MaxPlatformCostUsdPerDay)}' must be greater than zero.");
        }

        if (options.MaxPlatformCostUsdPerMonth <= 0m)
        {
            problems.Add($"'{SectionName}:{nameof(MaxPlatformCostUsdPerMonth)}' must be greater than zero.");
        }

        return problems;
    }
}
