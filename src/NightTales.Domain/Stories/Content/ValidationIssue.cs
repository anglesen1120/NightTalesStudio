namespace NightTales.Domain.Stories.Content;

/// <summary>How hard a <see cref="ValidationIssue"/> blocks a run (02 README D3, D22 <c>advisory</c>/<c>blocking</c>).</summary>
public enum IssueSeverity
{
    /// <summary>Reported, and the run continues (the vocabulary check is the only local advisory).</summary>
    Advisory,

    /// <summary>Blocking: the job maps at least one of these to an invalid output (D21, D7).</summary>
    Blocking,
}

/// <summary>Where an issue comes from (02 README D22, <c>audit.v1</c> <c>kind</c>).</summary>
public enum IssueKind
{
    /// <summary>A machine check of this validator; every issue it returns has this kind.</summary>
    Objective,

    /// <summary>A taste finding of the model review (02.6.1); never produced by this validator.</summary>
    Taste,
}

/// <summary>
/// One D3 validation issue, shaped for <c>audit.v1</c> (02 README D22) and for the corrective note
/// (02.2.3-T2), which lists the <see cref="RuleId"/>s only. The validator reports and never repairs, so
/// the draft is never silently edited.
/// </summary>
/// <param name="RuleId">The stable rule id the corrective note and the audit report carry.</param>
/// <param name="Severity">Whether the issue blocks the run.</param>
/// <param name="Kind">The origin of the issue.</param>
/// <param name="Segment">The D22 address (<see cref="SegmentAddress"/>) the issue belongs to.</param>
/// <param name="Evidence">A fragment of our own draft (never source text) plus the measured value.</param>
/// <param name="RepairDirection">What the writer should change; guidance, not a rewrite.</param>
public sealed record ValidationIssue(
    string RuleId,
    IssueSeverity Severity,
    IssueKind Kind,
    string Segment,
    string Evidence,
    string RepairDirection)
{
    /// <summary>A blocking, objective issue; a null evidence becomes the empty string.</summary>
    /// <param name="ruleId">The stable rule id.</param>
    /// <param name="segment">The D22 address.</param>
    /// <param name="evidence">The draft fragment and the measured value.</param>
    /// <param name="repairDirection">What the writer should change.</param>
    /// <returns>The issue.</returns>
    public static ValidationIssue Blocking(string ruleId, string segment, string? evidence, string repairDirection) =>
        new(ruleId, IssueSeverity.Blocking, IssueKind.Objective, segment, evidence ?? string.Empty, repairDirection);

    /// <summary>An advisory, objective issue: reported, and the run continues.</summary>
    /// <param name="ruleId">The stable rule id.</param>
    /// <param name="segment">The D22 address.</param>
    /// <param name="evidence">The draft fragment and the measured value.</param>
    /// <param name="repairDirection">What the writer should change.</param>
    /// <returns>The issue.</returns>
    public static ValidationIssue Advisory(string ruleId, string segment, string? evidence, string repairDirection) =>
        new(ruleId, IssueSeverity.Advisory, IssueKind.Objective, segment, evidence ?? string.Empty, repairDirection);
}

/// <summary>
/// The D22 segment addressing shared by the editor (10.5.10), the validator, the checkers and the
/// similarity scorer: <c>title</c>, <c>hook</c>, <c>p:&lt;0-based index&gt;</c>, <c>ending</c> and
/// <c>meta</c> (everything document-level).
/// </summary>
public static class SegmentAddress
{
    /// <summary>The title.</summary>
    public const string Title = "title";

    /// <summary>The hook segment.</summary>
    public const string Hook = "hook";

    /// <summary>The ending segment.</summary>
    public const string Ending = "ending";

    /// <summary>Document-level fields: description, tags, structure, length, segment count.</summary>
    public const string Meta = "meta";

    /// <summary>The address of the paragraph at <paramref name="index"/>, zero-based.</summary>
    /// <param name="index">The paragraph index in D3 order.</param>
    /// <returns>The D22 address, e.g. <c>p:0</c>.</returns>
    public static string Paragraph(int index) => FormattableString.Invariant($"p:{index}");
}
