namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The D21 step-5 decision (02.6.2-T1): whether the run revises the draft once, and which issues the
/// revision call must fix. A revision happens only when the review holds at least one <c>blocking</c>
/// objective issue, the run still has a revision left and none was applied yet. Taste and advisory issues
/// are never revised (they stay hints). A null review, or a review with no issues, means "no issues", never
/// a crash.
/// </summary>
public static class RevisionPolicy
{
    private const string Blocking = "blocking";

    /// <summary>
    /// Whether the run revises the draft:
    /// <c>!revisionAlreadyApplied &amp;&amp; maxRevisions &gt; 0 &amp;&amp; BlockingObjectiveIssues(review).Count &gt; 0</c>.
    /// </summary>
    /// <param name="review">The validated model review; <see langword="null"/> is treated as "no issues".</param>
    /// <param name="maxRevisions">The configured bound on revision passes; a non-positive value disables revision (D21).</param>
    /// <param name="revisionAlreadyApplied">Whether a revision was already applied to this run's draft.</param>
    /// <returns><see langword="true"/> when exactly one revision pass must run.</returns>
    public static bool ShouldRevise(StoryReviewV1? review, int maxRevisions, bool revisionAlreadyApplied) =>
        !revisionAlreadyApplied && maxRevisions > 0 && BlockingObjectiveIssues(review).Count > 0;

    /// <summary>
    /// The issues the revision call must fix: the issues whose <c>severity</c> is <c>blocking</c> and whose
    /// type is an objective type, in review order. A taste issue is never returned, even when a malformed
    /// review marked it <c>blocking</c>; an issue whose type is outside the closed list is treated as
    /// non-objective rather than throwing.
    /// </summary>
    /// <param name="review">The validated model review; <see langword="null"/> is treated as "no issues".</param>
    /// <returns>The blocking objective issues, or an empty list.</returns>
    public static IReadOnlyList<ReviewIssueV1> BlockingObjectiveIssues(StoryReviewV1? review)
    {
        if (review?.Issues is not { Count: > 0 } issues)
        {
            return [];
        }

        var blocking = new List<ReviewIssueV1>(issues.Count);
        foreach (var issue in issues)
        {
            if (issue is not null
                && string.Equals(issue.Severity, Blocking, StringComparison.Ordinal)
                && IsObjectiveType(issue.Type))
            {
                blocking.Add(issue);
            }
        }

        return blocking;
    }

    // The same objective/taste split as ReviewIssueTypes.KindOf, but fail-closed on an unknown type instead of throwing.
    private static bool IsObjectiveType(string type) =>
        ReviewIssueTypes.TryParse(type, out var parsed)
            ? ReviewIssueTypes.IsObjective(parsed)
            : string.Equals(type, ReviewIssueTypes.FactualCareMissing, StringComparison.Ordinal);
}
