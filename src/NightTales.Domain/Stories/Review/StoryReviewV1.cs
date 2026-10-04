namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The <c>review.v1</c> model output (02 README D21, 02.6.1-T1): the seven D16 scores, a closed issue list,
/// the naturalness answers, the true-crime <c>factual_care</c> block and the real-world claims. The strict
/// schema is <c>prompts/schemas/review.v1.json</c>.
/// </summary>
/// <param name="Scores">The D16 scores.</param>
/// <param name="Issues">The issues, most serious first.</param>
/// <param name="EarRuleAnswers">One answer per naturalness question.</param>
/// <param name="FactualCare">The factual-care block; <see langword="null"/> outside true crime.</param>
/// <param name="RealWorldClaims">The places that present something as real; empty when none.</param>
/// <param name="Summary">One or two sentences; carries no instruction.</param>
public sealed record StoryReviewV1(
    ReviewScores Scores,
    IReadOnlyList<ReviewIssueV1> Issues,
    IReadOnlyList<EarRuleAnswer> EarRuleAnswers,
    FactualCareReview? FactualCare,
    IReadOnlyList<RealWorldClaim> RealWorldClaims,
    string Summary);

/// <summary>The true-crime <c>factual_care</c> block of a <c>review.v1</c>.</summary>
/// <param name="ClaimsChecked">How many checkable claims the reviewer counted.</param>
/// <param name="UnsupportedClaims">The claims no supplied fact supports.</param>
/// <param name="Notes">One short sentence.</param>
public sealed record FactualCareReview(
    int ClaimsChecked,
    IReadOnlyList<UnsupportedClaim> UnsupportedClaims,
    string Notes);

/// <summary>One claim of the draft that no supplied fact supports.</summary>
/// <param name="Segment">The D22 address of the segment.</param>
/// <param name="Claim">The claim, quoted from the draft.</param>
public sealed record UnsupportedClaim(string Segment, string Claim);
