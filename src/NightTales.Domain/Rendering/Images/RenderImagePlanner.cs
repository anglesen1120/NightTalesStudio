using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Domain.Rendering.Images;

/// <summary>
/// The pure, deterministic image plan of one render (03 README D3, F03, F17): how many images the narration needs,
/// how many keywords are sent (needed plus free spares, never above the tier maximum) and which keywords survive
/// the cap. It never reorders and never edits a keyword.
/// </summary>
public static class RenderImagePlanner
{
    /// <summary>Hint: the cap removed keywords (advisory).</summary>
    public const string ScenesTrimmed = "scenes_trimmed";

    /// <summary>Hint: fewer terms than the sparse line of the needed images, so MPT will loop images (advisory).</summary>
    public const string ScenesSparse = "scenes_sparse";

    /// <summary>Plans the images of one render.</summary>
    /// <param name="input">The keywords and narration size.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A clip length, rate or token count is not positive, or a share is outside 0..1.</exception>
    public static RenderImagePlan Plan(RenderImagePlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Segments);
        ArgumentNullException.ThrowIfNull(input.Options);

        var options = input.Options;
        ArgumentOutOfRangeException.ThrowIfNegative(input.ScriptTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ClipSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RateVi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RateEn);
        ArgumentOutOfRangeException.ThrowIfNegative(options.SpareShare);
        ArgumentOutOfRangeException.ThrowIfNegative(options.SparseShare);

        // Integer arithmetic for the seconds, decimal for the shares: 10 × 0.7 must be 7, not 7.000000000000001.
        long rate = input.Language == Language.Vi ? options.RateVi : options.RateEn;
        var needed = (int)Math.Max(1, CeilDiv(input.ScriptTokens * 60L, rate * options.ClipSeconds));
        var tierCap = (int)CeilDiv(TierMaxMinutes(input.Tier) * 60L, options.ClipSeconds);
        var maxTerms = (int)Math.Min(CeilShare(needed, 1m + (decimal)options.SpareShare), tierCap);

        var entries = new List<Entry>();
        for (var segment = 0; segment < input.Segments.Count; segment++)
        {
            var keywords = input.Segments[segment].Keywords;
            var index = 0;
            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    entries.Add(new Entry(segment, index++, input.Segments[segment].Segment, keyword));
                }
            }
        }

        var trimmed = Math.Max(0, entries.Count - maxTerms);
        if (trimmed > 0)
        {
            // Second keywords first (last segment backwards), then first keywords (last segment backwards).
            var drop = entries
                .OrderBy(entry => entry.Index == 0 ? 1 : 0)
                .ThenByDescending(entry => entry.SegmentIndex)
                .ThenByDescending(entry => entry.Index)
                .Take(trimmed)
                .ToHashSet();
            entries.RemoveAll(drop.Contains);
        }

        var hints = new List<string>(2);
        if (trimmed > 0)
        {
            hints.Add(ScenesTrimmed);
        }

        if (entries.Count < CeilShare(needed, (decimal)options.SparseShare))
        {
            hints.Add(ScenesSparse);
        }

        var terms = entries.Select((entry, slot) => new PlannedTerm(slot, entry.Segment, entry.Keyword)).ToList();

        return new RenderImagePlan(terms, options.ClipSeconds, needed, maxTerms, trimmed, hints);
    }

    private static int TierMaxMinutes(LengthTier tier) => tier switch
    {
        LengthTier.Short => 1,
        LengthTier.Standard => 8,
        LengthTier.Long => 15,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown length tier."),
    };

    private static long CeilDiv(long numerator, long denominator) => (numerator + denominator - 1) / denominator;

    private static long CeilShare(int count, decimal share) => (long)decimal.Ceiling(count * share);

    private sealed record Entry(int SegmentIndex, int Index, string Segment, string Keyword);
}
