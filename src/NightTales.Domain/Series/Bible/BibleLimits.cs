namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The content limits of a series bible (02.6.3 plan §3.2), bound from the <c>Bible:Limits</c> section and
/// registered as a singleton, so a handler injects the record rather than the options type (the Application
/// layer cannot reference Infrastructure). Every value is also the record's own default, so a host whose
/// configuration omits the section still enforces the plan's numbers.
/// </summary>
/// <remarks>
/// <para>
/// The limits are enforced by <see cref="BibleContentValidator"/>; nothing else may re-implement them. The
/// defender of the D6 data-object budget is <see cref="MaxContentBytes"/>: the whole serialised
/// <c>bible.v1</c> document, measured in UTF-8 bytes.
/// </para>
/// <para>
/// <see cref="NameChars"/> caps the <c>name</c> of a character and a place, and is deliberately narrower than
/// <see cref="NarratorNameChars"/>: it is what gives the 02.6.3-T3 writer-snapshot budget a provable upper
/// bound on every field (that task's convergence proof).
/// </para>
/// </remarks>
public sealed record BibleLimits
{
    /// <summary>The longest <c>narrator.name</c>.</summary>
    public int NarratorNameChars { get; init; } = 80;

    /// <summary>The longest <c>narrator.persona</c>; the field must also be non-empty.</summary>
    public int NarratorPersonaChars { get; init; } = 1000;

    /// <summary>The longest <c>narrator.voice_notes</c>.</summary>
    public int VoiceNotesChars { get; init; } = 1000;

    /// <summary>The most <c>recurring_characters</c> entries.</summary>
    public int MaxCharacters { get; init; } = 30;

    /// <summary>The most <c>places</c> entries.</summary>
    public int MaxPlaces { get; init; } = 30;

    /// <summary>The most <c>rules</c> entries.</summary>
    public int MaxRules { get; init; } = 30;

    /// <summary>The longest <c>name</c> of a character or a place.</summary>
    public int NameChars { get; init; } = 60;

    /// <summary>The longest <c>description</c> of a character or a place.</summary>
    public int DescriptionChars { get; init; } = 300;

    /// <summary>The longest <c>text</c> of a rule.</summary>
    public int RuleChars { get; init; } = 300;

    /// <summary>The longest <c>title</c> of an episode summary.</summary>
    public int SummaryTitleChars { get; init; } = 100;

    /// <summary>
    /// The longest <c>summary</c> of an episode summary. A manual PUT beyond it is a 400; a proposal op beyond
    /// it is truncated on a word boundary instead (plan §4 U4).
    /// </summary>
    public int SummaryChars { get; init; } = 200;

    /// <summary>The largest canonical <c>bible.v1</c> document, in UTF-8 bytes (the D6 data-object budget).</summary>
    public int MaxContentBytes { get; init; } = 65536;
}
