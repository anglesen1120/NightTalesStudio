using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Quality;

/// <summary>One <c>audit.v1</c> <c>local.issues</c> entry (02 README D22); ids and codes only, no copy.</summary>
/// <param name="Rule">The rule id.</param>
/// <param name="Severity"><c>blocking</c> or <c>advisory</c>.</param>
/// <param name="Kind"><c>objective</c> or <c>taste</c>.</param>
/// <param name="Segment">The D22 segment address.</param>
/// <param name="Evidence">At most 200 characters of our own draft.</param>
/// <param name="RepairDirection">A short imperative, or null.</param>
public sealed record AuditLocalIssue(string Rule, string Severity, string Kind, string Segment, string Evidence, string? RepairDirection);

/// <summary>One editor hint (02.3.4-T2 <c>qualityHints[]</c>): an issue plus its i18n key and numbers.</summary>
/// <param name="RuleId">The rule id.</param>
/// <param name="Severity"><c>blocking</c> or <c>advisory</c>.</param>
/// <param name="Kind"><c>objective</c> or <c>taste</c>.</param>
/// <param name="Segment">The D22 segment address.</param>
/// <param name="MessageKey">The i18n key.</param>
/// <param name="Evidence">At most 200 characters of our own draft.</param>
/// <param name="RepairDirection">A short imperative, or null.</param>
/// <param name="Measured">The measured value, or null.</param>
/// <param name="Threshold">The threshold, or null.</param>
public sealed record QualityHintDto(
    string RuleId,
    string Severity,
    string Kind,
    string Segment,
    string MessageKey,
    string Evidence,
    string? RepairDirection,
    decimal? Measured,
    decimal? Threshold);

/// <summary>The two projections of a <see cref="QualityReport"/> (02.3.4-T1 step 4): the stored D22 shape and the editor hints.</summary>
public static class QualityReportMapping
{
    /// <summary>The D22 <c>audit_report.local.issues</c> entries, in the report's stable order.</summary>
    /// <param name="report">The checker's report.</param>
    /// <returns>One entry per issue.</returns>
    public static IReadOnlyList<AuditLocalIssue> ToAuditIssues(this QualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Issues
            .Select(issue => new AuditLocalIssue(
                issue.RuleId,
                Wire(issue.Severity),
                Wire(issue.Kind),
                issue.Segment,
                issue.Evidence,
                issue.RepairDirection))
            .ToList();
    }

    /// <summary>The editor hints: objective first, otherwise the report's stable order.</summary>
    /// <param name="report">The checker's report.</param>
    /// <returns>One hint per issue.</returns>
    public static IReadOnlyList<QualityHintDto> ToHints(this QualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Issues
            .OrderBy(issue => issue.Kind == QualityKind.Objective ? 0 : 1)
            .Select(issue => new QualityHintDto(
                issue.RuleId,
                Wire(issue.Severity),
                Wire(issue.Kind),
                issue.Segment,
                issue.MessageKey,
                issue.Evidence,
                issue.RepairDirection,
                issue.Measured,
                issue.Threshold))
            .ToList();
    }

    private static string Wire(QualitySeverity severity) =>
        severity == QualitySeverity.Blocking ? "blocking" : "advisory";

    private static string Wire(QualityKind kind) =>
        kind == QualityKind.Objective ? "objective" : "taste";
}
