namespace NightTales.Domain.Stories.Content;

/// <summary>
/// Maps the writer's free-text structure values onto the run's vocabulary (02 README D3, 02 T71) and is
/// the <b>sole</b> owner of the <c>other:</c> prefix: the model emits a plain short string, a
/// model-supplied <c>other:</c> is stripped before mapping, and only this mapper writes
/// <c>other:&lt;text&gt;</c>.
/// </summary>
/// <remarks>
/// The vocabulary check covers <c>sub_genre</c>, <c>opening</c> and <c>twist_type</c>
/// (<see cref="StoryRules.StructureVocab"/>); <c>setting</c> is a free string and gets only the
/// length/URL/markup check. D3's structure block is document-level, so its issues carry the D22
/// <see cref="SegmentAddress.Meta"/> address, and an unknown term is advisory only — the variety guard
/// (02.8.4) must never fail a run on vocabulary.
/// </remarks>
public static class StructureVocabMapper
{
    /// <summary>The prefix only this mapper may add (02 T71).</summary>
    public const string OtherPrefix = "other:";

    /// <summary>
    /// Maps one vocabulary-checked structure term. A term outside
    /// <see cref="StoryRules.StructureVocab"/> is reported as an advisory <c>structure_vocab_unknown</c>
    /// and stored as <c>other:&lt;text&gt;</c>; a term longer than
    /// <see cref="StoryRules.StructureFreeStringMaxChars"/> or carrying a URL/markup is additionally a
    /// blocking <c>structure_text_invalid</c>.
    /// </summary>
    /// <param name="value">The writer's <c>sub_genre</c>, <c>opening</c> or <c>twist_type</c> value.</param>
    /// <param name="rules">The run's rules, including the topic's vocabulary.</param>
    /// <returns>The mapped value and the issues mapping it raised.</returns>
    public static StructureMapResult MapTerm(string? value, StoryRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var text = StripOtherPrefix(value);
        var issues = new List<ValidationIssue>();
        AddTextIssues(text, rules, issues);

        if (rules.StructureVocab.Contains(text))
        {
            return new StructureMapResult(text, issues);
        }

        issues.Add(ValidationIssue.Advisory(
            StoryDraftValidator.StructureVocabUnknown,
            SegmentAddress.Meta,
            text.Length == 0 ? "(empty)" : ValidationIssue.Snip(text),
            "Use one of the topic's vocabulary terms; an unmapped value is stored as other:<text>."));

        return new StructureMapResult(OtherPrefix + text, issues);
    }

    /// <summary>Checks a free-text structure value (<c>setting</c>): the length/URL/markup rule only.</summary>
    /// <param name="value">The writer's <c>setting</c> value.</param>
    /// <param name="rules">The run's rules.</param>
    /// <returns>The value unchanged, plus any issue the free-string rules raised.</returns>
    public static StructureMapResult MapFreeText(string? value, StoryRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var text = (value ?? string.Empty).Trim();
        var issues = new List<ValidationIssue>();
        AddTextIssues(text, rules, issues);
        return new StructureMapResult(text, issues);
    }

    /// <summary>Strips a model-supplied <c>other:</c> prefix, so the mapper never doubles it (02 T71).</summary>
    private static string StripOtherPrefix(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith(OtherPrefix, StringComparison.OrdinalIgnoreCase)
            ? text[OtherPrefix.Length..].Trim()
            : text;
    }

    /// <summary>The 40-character and URL/markup checks every unmapped structure value must pass (02 T71).</summary>
    private static void AddTextIssues(string text, StoryRules rules, List<ValidationIssue> issues)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > rules.StructureFreeStringMaxChars)
        {
            issues.Add(ValidationIssue.Blocking(
                StoryDraftValidator.StructureTextInvalid,
                SegmentAddress.Meta,
                ValidationIssue.Snip(text),
                FormattableString.Invariant($"Use at most {rules.StructureFreeStringMaxChars} characters.")));
        }

        if (StoryText.FindMarkup(text) is { } markup)
        {
            issues.Add(ValidationIssue.Blocking(
                StoryDraftValidator.StructureTextInvalid,
                SegmentAddress.Meta,
                FormattableString.Invariant($"'{ValidationIssue.Snip(markup)}' is not allowed in a structure value."),
                "Remove URLs and markup from the structure value."));
        }
    }
}

/// <summary>
/// The mapped structure value plus the issues mapping it raised (02 T71): the value is either a
/// vocabulary entry, the original free text (<c>setting</c>), or <c>other:&lt;text&gt;</c>.
/// </summary>
/// <param name="Value">The value to store.</param>
/// <param name="Issues">The advisory/blocking issues the mapping produced.</param>
public sealed record StructureMapResult(string Value, IReadOnlyList<ValidationIssue> Issues);
