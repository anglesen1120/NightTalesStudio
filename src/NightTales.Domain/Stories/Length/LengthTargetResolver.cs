using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Usage;

namespace NightTales.Domain.Stories.Length;

/// <summary>
/// Resolves the D21 signature <c>LengthTarget Resolve(LengthTier, Language, PriceLimits)</c> from the
/// active price version's <c>limits.story</c> table (02 README D4/D21). The bands and caps live in
/// <c>price_versions.limits</c>, not in code, so a re-calibrated band is a new price-version row.
/// </summary>
public static class LengthTargetResolver
{
    /// <summary>
    /// The length contract of one tier × language cell. A missing cell is a configuration error (the
    /// table is seeded from the same D4 source as the prompt catalog), so it throws rather than
    /// defaulting to a band nobody calibrated.
    /// </summary>
    /// <param name="tier">The episode's length tier.</param>
    /// <param name="lang">The story's content language; the D4 bands are per language (02 T103).</param>
    /// <param name="limits">The active price version's limits.</param>
    /// <returns>The resolved token/segment bands and the three output caps.</returns>
    /// <exception cref="KeyNotFoundException">The price version has no cell for the tier × language pair.</exception>
    public static LengthTarget Resolve(LengthTier tier, Language lang, PriceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (!limits.Story.TryGetValue(tier, out var byLanguage)
            || !byLanguage.TryGetValue(lang, out var limit))
        {
            throw new KeyNotFoundException(
                FormattableString.Invariant($"price_versions.limits.story has no {tier}/{lang} cell (02 README D4/D21)."));
        }

        return new LengthTarget(
            limit.MinTokens,
            limit.MaxTokens,
            limit.MinSegments,
            limit.MaxSegments,
            limit.WriterMaxOutput,
            limit.ReviewMaxOutput,
            limit.RevisionMaxOutput);
    }
}
