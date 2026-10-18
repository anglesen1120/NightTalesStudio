namespace NightTales.Application.Features.VideoReview;

/// <summary>
/// Checks <see cref="AssetRetentionOptions"/> so a bad <c>Assets:RetentionDays</c> stops a host's start-up
/// instead of the first approve (the <c>RenderIngestOptionsValidator</c> precedent).
/// </summary>
/// <remarks>
/// The window is a promise to the owner about how long an accepted cut is kept, so an impossible value is
/// refused rather than clamped: a zero-day window would let the 09.7.3 sweep delete an asset in the same
/// instant the approve stamped it, and an unbounded one would keep paying for storage the plan never agreed
/// to. The task fixes the range as 1..365 days.
/// </remarks>
public static class AssetRetentionOptionsValidator
{
    /// <summary>The longest accepted retention window: one year.</summary>
    public const int MaxRetentionDays = 365;

    /// <summary>
    /// Validates the bound options: a retention window of at least one day and at most
    /// <see cref="MaxRetentionDays"/>.
    /// </summary>
    /// <param name="options">The bound options.</param>
    /// <returns>One message per problem; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(AssetRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RetentionDays is < 1 or > MaxRetentionDays)
        {
            return [$"{nameof(options.RetentionDays)} must be between 1 and {MaxRetentionDays}."];
        }

        return [];
    }
}
