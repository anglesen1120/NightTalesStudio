namespace NightTales.Application.Abstractions.Moderation;

/// <summary>
/// The <c>OpenAi:Moderation</c> section (02 README D12) plus the D12 blocking-category classification.
/// A plain settings object: Application does not reference <c>Microsoft.Extensions.Options</c>, so
/// Infrastructure binds the section and hands over the value.
/// </summary>
public sealed class ModerationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "OpenAi:Moderation";

    /// <summary>The default moderation model (02 README O7, free).</summary>
    public const string DefaultModel = "omni-moderation-latest";

    /// <summary>
    /// The floor of <see cref="MaxCharsPerChunk"/> (CR-3/SEC-03): under it the word-edge cuts and the
    /// inter-chunk overlap that keep a flag from hiding across a boundary cannot work, and a cap of one
    /// character would split a surrogate pair into invalid UTF-16.
    /// </summary>
    public const int MinCharsPerChunk = 500;

    /// <summary>
    /// The sentinel the moderation port adds to the flagged categories when a result says <c>flagged</c> but
    /// carries no category the omni table knows (absent, all-false or renamed categories: schema drift or a
    /// hostile answer). It blocks approval exactly like a D12 name (SEC-01).
    /// </summary>
    public const string UnclassifiedCategory = "unclassified";

    /// <summary>The D12 default blocking categories for the human path (a draft carrying one cannot be approved).</summary>
    public static IReadOnlySet<string> DefaultBlockingCategories { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "sexual/minors",
        "self-harm/instructions",
        "illicit/violent",
        "hate/threatening",
    };

    /// <summary>
    /// The omni-moderation category table (02 README O7). The port needs it to tell a known-but-advisory flag
    /// from a flagged result whose categories are all names the provider contract does not define: only the
    /// latter becomes <see cref="UnclassifiedCategory"/>.
    /// </summary>
    public static IReadOnlySet<string> KnownCategories { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "harassment",
        "harassment/threatening",
        "hate",
        "hate/threatening",
        "illicit",
        "illicit/violent",
        "self-harm",
        "self-harm/intent",
        "self-harm/instructions",
        "sexual",
        "sexual/minors",
        "violence",
        "violence/graphic",
    };

    /// <summary>Gets or sets the moderation model.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Gets or sets the longest text sent in one input, in characters; a longer text is split (no documented limit, O7).</summary>
    public int MaxCharsPerChunk { get; set; } = 8000;

    /// <summary>Gets or sets the per-attempt timeout in seconds (TL T21: keeps the worst-case job inside <c>Story:Job:TimeoutSeconds</c>).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Gets the categories that block approval of a draft; every other flagged category is advisory. Set in code, not bound from configuration.</summary>
    public IReadOnlySet<string> BlockingCategories { get; init; } = DefaultBlockingCategories;

    /// <summary>Splits the flagged categories into blocking ones and advisory hints (D12).</summary>
    /// <param name="categories">The flagged categories of a <see cref="ModerationResult"/>.</param>
    /// <returns>The classification.</returns>
    public ModerationClassification Classify(IEnumerable<string> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var blocking = new List<string>();
        var advisory = new List<string>();

        foreach (var category in categories.Distinct(StringComparer.Ordinal))
        {
            // SEC-01: the `unclassified` sentinel means "the provider flagged the text yet no known category
            // fired", so it blocks like a D12 name. Any other name outside BlockingCategories (advisory or
            // provider-new) is a hint only; an unknown name alone blocks only when the result was flagged
            // with no known category at all, which is exactly the case the port encodes as the sentinel.
            (category == UnclassifiedCategory || BlockingCategories.Contains(category) ? blocking : advisory).Add(category);
        }

        return new ModerationClassification(blocking, advisory);
    }
}

/// <summary>The flagged categories of a draft, split by D12 (02.4.1-T1 step 5): what <c>audit.v1.moderation</c> carries.</summary>
/// <param name="Blocking">The categories that block approval; the run still succeeds.</param>
/// <param name="Advisory">The categories that are a hint only.</param>
public sealed record ModerationClassification(IReadOnlyList<string> Blocking, IReadOnlyList<string> Advisory)
{
    /// <summary>Gets a value indicating whether approval is blocked.</summary>
    public bool BlocksApproval => Blocking.Count > 0;
}
