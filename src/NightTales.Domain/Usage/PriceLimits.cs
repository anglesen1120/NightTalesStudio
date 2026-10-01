using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Domain.Usage;

/// <summary>
/// The story limits of one <c>price_versions</c> row (<c>limits.story</c>, 02 README D4/D14): one
/// <see cref="LengthLimit"/> per <see cref="LengthTier"/> × <see cref="Language"/> cell. This is the C#
/// mirror of the stored JSON document, so a re-calibrated band is a new price-version row, never a code
/// change; 02.2.4-T1 serialises it with a string-enum converter.
/// </summary>
/// <param name="Story">The per-tier, per-language story limits of one price version.</param>
public sealed record PriceLimits(
    IReadOnlyDictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>> Story)
{
    /// <summary>
    /// The D4 table (02 README D4, T103 bands): token bands, segment bands and the writer/review/revision
    /// output caps, per tier × language. <c>trial/tools/LENGTH.v1.json</c> is the machine copy of the
    /// token and segment bands; the review and revision caps come from the D4 table (short 1,200/1,500,
    /// standard 2,500/6,000, long 3,500/10,000) and are not in that JSON.
    /// </summary>
    public static PriceLimits D4Defaults { get; } = new(
        new Dictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>>
        {
            [LengthTier.Short] = new Dictionary<Language, LengthLimit>
            {
                [Language.Vi] = new(195, 260, 4, 5, 2_500, 1_200, 1_500),
                [Language.En] = new(150, 200, 4, 5, 2_500, 1_200, 1_500),
            },
            [LengthTier.Standard] = new Dictionary<Language, LengthLimit>
            {
                [Language.Vi] = new(1_300, 2_080, 20, 32, 6_000, 2_500, 6_000),
                [Language.En] = new(1_000, 1_600, 20, 32, 6_000, 2_500, 6_000),
            },
            [LengthTier.Long] = new Dictionary<Language, LengthLimit>
            {
                [Language.Vi] = new(2_600, 3_900, 40, 60, 10_000, 3_500, 10_000),
                [Language.En] = new(2_000, 3_000, 40, 60, 10_000, 3_500, 10_000),
            },
        });
}

/// <summary>One <c>limits.story</c> cell: the D4 token and segment bands plus the three per-step output caps.</summary>
/// <param name="MinTokens">The lower bound of the token band.</param>
/// <param name="MaxTokens">The upper bound of the token band.</param>
/// <param name="MinSegments">The lower bound of the segment band (hook + paragraphs + ending).</param>
/// <param name="MaxSegments">The upper bound of the segment band.</param>
/// <param name="WriterMaxOutput">The <c>max_output_tokens</c> of the writing call.</param>
/// <param name="ReviewMaxOutput">The <c>max_output_tokens</c> of the reviewing call.</param>
/// <param name="RevisionMaxOutput">The <c>max_output_tokens</c> of the revising call.</param>
public sealed record LengthLimit(
    int MinTokens,
    int MaxTokens,
    int MinSegments,
    int MaxSegments,
    int WriterMaxOutput,
    int ReviewMaxOutput,
    int RevisionMaxOutput);
