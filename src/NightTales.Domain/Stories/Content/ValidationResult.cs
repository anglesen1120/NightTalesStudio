namespace NightTales.Domain.Stories.Content;

/// <summary>
/// The D21 step-2 result of <see cref="StoryDraftValidator"/>: the complete issue list, never truncated.
/// The job (02.2.2-T3), not this type, decides that a draft with a blocking issue is an invalid output and
/// owns the single retry (D21, D7).
/// </summary>
/// <param name="Issues">Every issue the validator found, in rule order.</param>
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    /// <summary>
    /// The distinct blocking rule ids, in first-seen order. This is exactly what the corrective note sends
    /// back as <c>corrective_note.violated_rules</c> (02.2.2-T3), with the invalid text never echoed.
    /// </summary>
    public IReadOnlyList<string> BlockingRuleIds { get; } = Issues
        .Where(issue => issue.Severity == IssueSeverity.Blocking)
        .Select(issue => issue.RuleId)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>True when no issue is blocking; an advisory-only result still lets the run continue.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity != IssueSeverity.Blocking);
}
