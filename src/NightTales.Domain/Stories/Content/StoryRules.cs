using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Usage;
using ContentLanguage = NightTales.Domain.Common.Language;

namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The D3/D4 limits one draft is validated against (02 README D3, D4, F36, P5): the objective limits, the
/// <see cref="LengthTarget"/> of the cell the job selected, and the run's data lists (approved fact ids,
/// the structure vocabulary of the topic, and the bible/fact-sheet person and place names R5/R12 match
/// against). The job (02.2.2) builds one instance per run from the active price version and the prompt
/// catalog.
/// </summary>
public sealed record StoryRules
{
    /// <summary>The D3 title limit.</summary>
    public int TitleMaxChars { get; init; } = 100;

    /// <summary>The D3 hook limit, in sentences.</summary>
    public int HookMaxSentences { get; init; } = 3;

    /// <summary>The D3 minimum keywords per segment.</summary>
    public int MinKeywordsPerSegment { get; init; } = 1;

    /// <summary>The D3 maximum keywords per segment.</summary>
    public int MaxKeywordsPerSegment { get; init; } = 2;

    /// <summary>The D3 description limit.</summary>
    public int DescriptionMaxChars { get; init; } = 1_000;

    /// <summary>The F36 tag count limit, the same one 06.2.1's upload validation uses.</summary>
    public int MaxTags { get; init; } = 15;

    /// <summary>The F36 per-tag length limit.</summary>
    public int TagMaxChars { get; init; } = 30;

    /// <summary>The F36 total tag length limit.</summary>
    public int TagsTotalMaxChars { get; init; } = 400;

    /// <summary>The 02 T71 limit on an unmapped structure free string.</summary>
    public int StructureFreeStringMaxChars { get; init; } = 40;

    /// <summary>
    /// The resolved length contract (D4/D21). The default is the <c>short</c>/<c>vi</c> cell — the
    /// cheapest tier, and the only band a hand-built draft can meet — so a caller that builds a
    /// <see cref="StoryRules"/> by hand gets the D4 numbers for the common test tier and varies the cell
    /// with a <c>with</c> expression.
    /// </summary>
    public LengthTarget Length { get; init; } =
        LengthTargetResolver.Resolve(LengthTier.Short, ContentLanguage.Vi, PriceLimits.D4Defaults);

    /// <summary>The content language of the run; the D4 bands are per language (02 T103).</summary>
    public ContentLanguage Language { get; init; } = ContentLanguage.Vi;

    /// <summary>The fact ids of the approved fact sheet; empty for a fiction topic (D3).</summary>
    public IReadOnlySet<string> AllowedFactRefs { get; init; } = new HashSet<string>();

    /// <summary>The <c>sub_genre</c>, <c>opening</c> and <c>twist_type</c> entries of the topic's structure vocabulary.</summary>
    public IReadOnlySet<string> StructureVocab { get; init; } = new HashSet<string>();

    /// <summary>The run's bible character names plus the approved fact-sheet person names (R5, 02 T85).</summary>
    public IReadOnlySet<string> KnownPersonNames { get; init; } = new HashSet<string>();

    /// <summary>The approved fact-sheet place names (R12, 02 T85).</summary>
    public IReadOnlySet<string> KnownPlaceNames { get; init; } = new HashSet<string>();
}
