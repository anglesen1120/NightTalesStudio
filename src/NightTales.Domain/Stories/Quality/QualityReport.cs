namespace NightTales.Domain.Stories.Quality;

/// <summary>The ordered issues of one check plus the counters it measured (02.3.4-T1, D21).</summary>
/// <param name="Issues">Issues in the fixed order (rule id ordinal, then segment position).</param>
/// <param name="Counters">What the checker measured.</param>
public sealed record QualityReport(IReadOnlyList<QualityIssue> Issues, QualityCounters Counters)
{
    /// <summary>Whether any issue is <see cref="QualitySeverity.Blocking"/>; the checker never gates approve on it.</summary>
    public bool HasBlocking => Issues.Any(issue => issue.Severity == QualitySeverity.Blocking);
}
