using System.Collections.Frozen;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The wire spelling, kind and severity rule of the <see cref="ReviewIssueType"/> list (02.6.1-T1). The kind is
/// derived here, server-side, so "blocking only for objective types" holds whatever the model returns.
/// </summary>
public static class ReviewIssueTypes
{
    /// <summary>
    /// The type of the advisory issue the server adds when a true-crime review carries no <c>factual_care</c>.
    /// It is not one of the model's seventeen types: the model can never return it.
    /// </summary>
    public const string FactualCareMissing = "factual_care_missing";

    private static readonly FrozenDictionary<string, ReviewIssueType> ByWire =
        Enum.GetValues<ReviewIssueType>().ToFrozenDictionary(Wire, StringComparer.Ordinal);

    /// <summary>The snake_case wire name of <paramref name="type"/>, for example <c>fact_ref_invalid</c>.</summary>
    /// <param name="type">The issue type.</param>
    /// <returns>The wire name.</returns>
    public static string Wire(ReviewIssueType type) => type switch
    {
        ReviewIssueType.FactRefInvalid => "fact_ref_invalid",
        ReviewIssueType.FactNotInSheet => "fact_not_in_sheet",
        ReviewIssueType.MissingSetupBeforeTwist => "missing_setup_before_twist",
        ReviewIssueType.ToneViolation => "tone_violation",
        ReviewIssueType.LengthOutOfRange => "length_out_of_range",
        ReviewIssueType.BibleContradiction => "bible_contradiction",
        ReviewIssueType.EarRuleViolation => "ear_rule_violation",
        ReviewIssueType.KeywordRuleViolation => "keyword_rule_violation",
        ReviewIssueType.HookMissing => "hook_missing",
        ReviewIssueType.EndingMissing => "ending_missing",
        ReviewIssueType.BeatMissing => "beat_missing",
        ReviewIssueType.MarkdownOrUrlInText => "markdown_or_url_in_text",
        ReviewIssueType.ContentWarningMissing => "content_warning_missing",
        ReviewIssueType.WeakHook => "weak_hook",
        ReviewIssueType.FlatEnding => "flat_ending",
        ReviewIssueType.PacingIssue => "pacing_issue",
        ReviewIssueType.RepetitivePhrasing => "repetitive_phrasing",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown review issue type."),
    };

    /// <summary>Parses a wire name; the match is exact and case-sensitive.</summary>
    /// <param name="wire">The wire name the model returned.</param>
    /// <param name="type">The type when the name is one of the seventeen.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is in the closed list.</returns>
    public static bool TryParse(string? wire, out ReviewIssueType type)
    {
        if (wire is not null && ByWire.TryGetValue(wire, out type))
        {
            return true;
        }

        type = default;
        return false;
    }

    /// <summary>Whether <paramref name="type"/> is an objective (checkable) type; the other four are taste.</summary>
    /// <param name="type">The issue type.</param>
    /// <returns><see langword="true"/> for the thirteen objective types.</returns>
    public static bool IsObjective(ReviewIssueType type) => type < ReviewIssueType.WeakHook;

    /// <summary>Whether an issue of <paramref name="type"/> may be <c>blocking</c>: objective types only.</summary>
    /// <param name="type">The issue type.</param>
    /// <returns><see langword="true"/> for objective types.</returns>
    public static bool IsBlockingAllowed(ReviewIssueType type) => IsObjective(type);

    /// <summary>The D22 <c>kind</c> of a wire type name: taste for the four taste types, objective otherwise.</summary>
    /// <param name="wire">A model type or <see cref="FactualCareMissing"/>.</param>
    /// <returns>The kind.</returns>
    /// <exception cref="ArgumentException">The name is neither a model type nor <see cref="FactualCareMissing"/>; a validated review never carries one.</exception>
    public static IssueKind KindOf(string wire)
    {
        if (string.Equals(wire, FactualCareMissing, StringComparison.Ordinal))
        {
            return IssueKind.Objective;
        }

        return TryParse(wire, out var type)
            ? (IsObjective(type) ? IssueKind.Objective : IssueKind.Taste)
            : throw new ArgumentException("The issue type is not in the closed review list.", nameof(wire));
    }
}
