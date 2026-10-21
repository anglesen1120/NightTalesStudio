namespace NightTales.Domain.Stories.Prompts;

/// <summary>
/// The shared prompt blocks of the D5 catalog (02 README D5) that a preset substitutes through its
/// closed placeholders — the ones "used by a preset", so the start-up gate requires every one of them
/// to be present in both languages where it varies.
/// </summary>
/// <remarks>
/// <para>
/// The members are the four language-varying or language-independent rule blocks of
/// <c>prompts/shared/</c>; <c>shared/preset-spec.v1.md</c> is not here because no preset references it
/// (it is the authoring spec of a preset, not a block a template carries).
/// </para>
/// <para>
/// A lookup takes the content language as well as the file because two of the four vary by language;
/// for <see cref="KeywordRules"/> and <see cref="StructureVocab"/> the language is ignored, which is
/// why the catalog takes it once for every member rather than splitting the enum in two.
/// </para>
/// </remarks>
public enum PromptFile
{
    /// <summary><c>shared/ear-rules.{vi|en}.v1.md</c> — the naturalness rules of the <c>{{ear_rules}}</c> block.</summary>
    EarRules,

    /// <summary><c>shared/overused.{vi|en}.v1.txt</c> — the <c>{{overused_phrases}}</c> block.</summary>
    Overused,

    /// <summary><c>shared/keyword-rules.v1.md</c> — the <c>{{keyword_rules}}</c> block.</summary>
    KeywordRules,

    /// <summary><c>shared/structure-vocab.v1.json</c> — the <c>{{structure_vocab}}</c> block.</summary>
    StructureVocab,
}
