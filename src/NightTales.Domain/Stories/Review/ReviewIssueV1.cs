namespace NightTales.Domain.Stories.Review;

/// <summary>
/// One issue of a <c>review.v1</c> as the model returns it (02.6.1-T1). <see cref="Type"/> and
/// <see cref="Severity"/> stay text so that a value outside the closed lists is a validator finding
/// (<see cref="ReviewResultValidator"/>) instead of a deserialisation failure; the issue's <c>kind</c> is never on the
/// wire, it is derived from <see cref="Type"/>.
/// </summary>
/// <param name="Id">The issue id within the review, for example <c>i1</c>; unique.</param>
/// <param name="Type">One of the seventeen <see cref="ReviewIssueType"/> wire names.</param>
/// <param name="Severity"><c>blocking</c> (objective types only) or <c>advisory</c>.</param>
/// <param name="Segment">The D22 address: <c>title</c>, <c>hook</c>, <c>ending</c>, <c>meta</c> or <c>p:N</c>.</param>
/// <param name="Evidence">A quote of our own draft, at most 200 characters.</param>
/// <param name="Impact">What the listener loses.</param>
/// <param name="RepairDirection">What to change; guidance, not a rewrite.</param>
public sealed record ReviewIssueV1(
    string Id,
    string Type,
    string Severity,
    string Segment,
    string Evidence,
    string Impact,
    string RepairDirection);
