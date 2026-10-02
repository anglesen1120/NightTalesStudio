namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// The closed placeholder list of the 02 prompt catalog (02 README D5), the one set of <c>{{name}}</c>
/// tokens a preset or a step prompt may carry.
/// </summary>
/// <remarks>
/// <para>
/// The list is closed on purpose: a template that uses anything else is a start-up violation
/// (<c>PromptCatalog</c> rule 6), because an unsubstituted placeholder reaches the model as literal
/// text. Adding a member is a catalog contract change — it needs a CR line and a re-hash of every file
/// that gains it.
/// </para>
/// <para>
/// Untrusted material never travels through these tokens (02 README D6): they carry system-set rule
/// blocks and length scalars only, and the brief, bible, facts and source text go in the JSON data
/// object of the user message.
/// </para>
/// </remarks>
public static class PromptPlaceholders
{
    /// <summary>The display name of the run's content language, for example <c>Vietnamese</c>.</summary>
    public const string LanguageName = "language_name";

    /// <summary>The tier's target length in minutes, for example <c>15</c>.</summary>
    public const string TierMinutes = "tier_minutes";

    /// <summary>The tier's allowed output-token range, for example <c>1300-2080</c>.</summary>
    public const string TokenRange = "token_range";

    /// <summary>The tier's allowed segment range, for example <c>20-32</c>.</summary>
    public const string SegmentRange = "segment_range";

    /// <summary>The <c>shared/keyword-rules.v1.md</c> block.</summary>
    public const string KeywordRules = "keyword_rules";

    /// <summary>The language's <c>shared/ear-rules.*.v1.md</c> block.</summary>
    public const string EarRules = "ear_rules";

    /// <summary>The language's <c>shared/overused.*.v1.txt</c> block.</summary>
    public const string OverusedPhrases = "overused_phrases";

    /// <summary>The <c>shared/structure-vocab.v1.json</c> block.</summary>
    public const string StructureVocab = "structure_vocab";

    /// <summary>Every placeholder name a template may use, compared with <see cref="StringComparer.Ordinal"/>.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        LanguageName,
        TierMinutes,
        TokenRange,
        SegmentRange,
        KeywordRules,
        EarRules,
        OverusedPhrases,
        StructureVocab,
    };

    /// <summary>Whether <paramref name="name"/> — the text between <c>{{</c> and <c>}}</c> — is in the list.</summary>
    /// <param name="name">The placeholder name without its braces.</param>
    /// <returns><see langword="true"/> for one of the eight names above.</returns>
    public static bool IsKnown(string name) => All.Contains(name);
}
