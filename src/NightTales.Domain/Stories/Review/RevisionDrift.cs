namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The segment-drift check of a single revision pass (02.6.2-T1, D22 <c>revision.drift</c>): whether the
/// revised draft changed segments the review did not ask it to change, or lost a segment. Advisory only;
/// it never blocks the run and never changes the episode state.
/// </summary>
/// <param name="SegmentCountChanged">The narrative segment count (hook + paragraphs + ending) differs between the two drafts, or a paragraph list or segment was missing.</param>
/// <param name="ChangedSegments">The D22 addresses (<c>hook</c>, <c>p:N</c>, <c>ending</c>) of the untouched segments that drifted, in story order, unique.</param>
/// <param name="AddedFindings">The local-checker blocking rule ids the revised draft raises that the original did not, sorted, unique.</param>
public sealed record RevisionDrift(
    bool SegmentCountChanged,
    IReadOnlyList<string> ChangedSegments,
    IReadOnlyList<string> AddedFindings)
{
    /// <summary>Whether anything drifted: a count change, a changed untouched segment, or a new blocking finding.</summary>
    public bool HasDrift => SegmentCountChanged || ChangedSegments.Count > 0 || AddedFindings.Count > 0;

    /// <summary>The drift of a revision that changed nothing the check can see.</summary>
    public static RevisionDrift None { get; } = new(SegmentCountChanged: false, [], []);
}
