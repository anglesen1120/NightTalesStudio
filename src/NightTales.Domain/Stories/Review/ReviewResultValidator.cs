using System.Text.RegularExpressions;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Review;

/// <summary>One finding of <see cref="ReviewResultValidator"/>: a stable rule id, where it was found and whether the validator repaired it.</summary>
/// <param name="RuleId">The stable rule id, for example <c>review_unknown_issue_type</c>; never review text.</param>
/// <param name="Path">Where in the review, for example <c>issues[2].type</c>.</param>
/// <param name="Repaired"><see langword="true"/> when the validator fixed the slip and the review stays usable (a warning); <see langword="false"/> when the review must be rejected.</param>
public sealed record ReviewViolation(string RuleId, string Path, bool Repaired);

/// <summary>The outcome of validating a <see cref="StoryReviewV1"/>: the review after the repairs, and every finding.</summary>
/// <param name="Review">The review with the repairable slips fixed; usable only when <see cref="IsAccepted"/>.</param>
/// <param name="Violations">Every finding, in a stable order.</param>
public sealed record ValidatedReview(StoryReviewV1 Review, IReadOnlyList<ReviewViolation> Violations)
{
    /// <summary>Whether the review is usable: it has no finding that the validator could not repair.</summary>
    public bool IsAccepted => Violations.All(violation => violation.Repaired);

    /// <summary>The findings that reject the review; their rule ids are what the invalid-output retry names.</summary>
    public IReadOnlyList<ReviewViolation> Rejections => [.. Violations.Where(violation => !violation.Repaired)];

    /// <summary>The findings the validator repaired; logged as warnings, never a run failure.</summary>
    public IReadOnlyList<ReviewViolation> Warnings => [.. Violations.Where(violation => violation.Repaired)];
}

/// <summary>
/// The post-call check of a <c>review.v1</c> (02.6.1-T1): the closed issue list, 1–5 scores, the D22 segment
/// address, the 200-character evidence cap and unique issue ids are hard contracts and reject the review;
/// a <c>blocking</c> taste issue, a <c>factual_care</c> block outside true crime and a missing one inside it
/// are model slips that are repaired and reported as warnings, so a run never fails on them. Pure and
/// deterministic; it reports finding ids only, never review text.
/// </summary>
public static partial class ReviewResultValidator
{
    /// <summary>Rule id: an issue <c>type</c> outside the closed list.</summary>
    public const string UnknownIssueType = "review_unknown_issue_type";

    /// <summary>Rule id: an issue <c>severity</c> that is neither <c>blocking</c> nor <c>advisory</c>.</summary>
    public const string UnknownSeverity = "review_unknown_severity";

    /// <summary>Rule id: a score outside 1–5.</summary>
    public const string ScoreOutOfRange = "review_score_out_of_range";

    /// <summary>Rule id: a segment that is not a D22 address of the draft.</summary>
    public const string BadSegment = "review_bad_segment";

    /// <summary>Rule id: an evidence or claim quote longer than 200 characters.</summary>
    public const string EvidenceTooLong = "review_evidence_too_long";

    /// <summary>Rule id: an empty or repeated issue id.</summary>
    public const string DuplicateIssueId = "review_duplicate_issue_id";

    /// <summary>Rule id: an answer for a question that was not asked, or two answers to one question.</summary>
    public const string BadEarRuleAnswer = "review_bad_ear_rule_answer";

    /// <summary>Rule id: a question that was asked has no answer in <c>ear_rule_answers</c> (CR-1).</summary>
    public const string MissingEarRuleAnswer = "review_missing_ear_rule_answer";

    /// <summary>Rule id: an issue id outside <c>^[a-z][a-z0-9_]{0,31}$</c> or one that uses the reserved <c>ear_</c> prefix of the synthesised ear-rule issues (CR-4, S2).</summary>
    public const string BadIssueId = "review_bad_issue_id";

    /// <summary>Rule id: a list longer than its cap (<see cref="MaxIssues"/>, <see cref="MaxEarRuleAnswers"/>, <see cref="MaxRealWorldClaims"/>) (S2).</summary>
    public const string TooManyItems = "review_too_many_items";

    /// <summary>Rule id: an <c>impact</c> or <c>repair_direction</c> longer than <see cref="MaxFieldChars"/> (S2).</summary>
    public const string FieldTooLong = "review_field_too_long";

    /// <summary>The most issues a review may list; the same cap as <c>review.v1.json</c> and the prompt.</summary>
    public const int MaxIssues = 20;

    /// <summary>The most naturalness answers a review may carry; one per question Q1–Q6.</summary>
    public const int MaxEarRuleAnswers = 6;

    /// <summary>The most <c>real_world_claims</c> a review may list.</summary>
    public const int MaxRealWorldClaims = 20;

    /// <summary>The longest <c>impact</c> or <c>repair_direction</c>, in characters.</summary>
    public const int MaxFieldChars = 300;

    /// <summary>Rule id (repaired): a <c>blocking</c> issue of a taste type, downgraded to <c>advisory</c>.</summary>
    public const string TasteIssueBlocking = "review_taste_issue_blocking";

    /// <summary>Rule id (repaired): a non-null <c>factual_care</c> outside true crime, cleared to <see langword="null"/>.</summary>
    public const string FactualCareForFiction = "review_factual_care_for_fiction";

    /// <summary>Rule id (repaired): a true-crime review without <c>factual_care</c>; an advisory issue is added.</summary>
    public const string FactualCareMissing = "review_factual_care_missing";

    /// <summary>Rule id (repaired): a failed naturalness answer without an <c>ear_rule_violation</c> issue; the issue is added.</summary>
    public const string EarRuleFailureWithoutIssue = "review_ear_rule_failure_without_issue";

    /// <summary>
    /// Whether <paramref name="id"/> has the shape of a review issue id (<c>^[a-z][a-z0-9_]{0,31}$</c>), the
    /// character contract every id of a review satisfies. The reserved <c>ear_</c> prefix is the collision rule
    /// between the model's ids and the synthesised ones, not a shape rule, so it is not part of this test.
    /// </summary>
    /// <param name="id">The candidate id.</param>
    /// <returns><see langword="true"/> when the id has the contract's shape.</returns>
    public static bool IsIssueId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);

    private const string Blocking = "blocking";
    private const string Advisory = "advisory";
    private const string ReservedIdPrefix = "ear_";

    /// <summary>Validates a review and repairs its model slips.</summary>
    /// <param name="review">The parsed model output.</param>
    /// <param name="topic">The series topic; <c>factual_care</c> exists for <see cref="Topic.TrueCrime"/> only.</param>
    /// <param name="paragraphCount">The number of paragraphs of the reviewed draft; a <c>p:N</c> segment must satisfy <c>N &lt; paragraphCount</c>.</param>
    /// <param name="questionIds">The ids of the naturalness questions that were asked.</param>
    /// <returns>The repaired review and every finding.</returns>
    public static ValidatedReview Validate(
        StoryReviewV1 review,
        Topic topic,
        int paragraphCount,
        IReadOnlyCollection<string> questionIds)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(questionIds);

        var found = new List<ReviewViolation>();

        CheckScores(review.Scores, found);
        CheckCount(review.Issues.Count, MaxIssues, "issues", found);
        CheckCount(review.EarRuleAnswers.Count, MaxEarRuleAnswers, "ear_rule_answers", found);
        CheckCount(review.RealWorldClaims.Count, MaxRealWorldClaims, "real_world_claims", found);

        var issues = new List<ReviewIssueV1>(review.Issues.Count + 2);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < review.Issues.Count; index++)
        {
            issues.Add(CheckIssue(review.Issues[index], index, paragraphCount, seenIds, found));
        }

        CheckAnswers(review.EarRuleAnswers, questionIds, paragraphCount, found);

        // CR-3: normalise factual_care for the topic first, so a block that is about to be cleared (fiction) is never checked and
        // cannot reject the review for its content.
        var scores = review.Scores;
        var factual = review.FactualCare;

        if (topic == Topic.TrueCrime)
        {
            if (scores.FactualCare is null || factual is null)
            {
                found.Add(new ReviewViolation(FactualCareMissing, "factual_care", Repaired: true));
                issues.Add(new ReviewIssueV1(
                    ReviewIssueTypes.FactualCareMissing,
                    ReviewIssueTypes.FactualCareMissing,
                    Advisory,
                    SegmentAddress.Meta,
                    string.Empty,
                    "The review did not assess factual care for a true-crime story.",
                    "Check the story's factual claims by hand before approving."));
            }
        }
        else if (scores.FactualCare is not null || factual is not null)
        {
            found.Add(new ReviewViolation(FactualCareForFiction, "factual_care", Repaired: true));
            scores = scores with { FactualCare = null };
            factual = null;
        }

        CheckClaims(review.RealWorldClaims, factual, paragraphCount, found);

        AddMissingEarRuleIssues(review.EarRuleAnswers, issues, found);

        var repaired = review with { Scores = scores, Issues = issues, FactualCare = factual };

        return new ValidatedReview(repaired, found);
    }

    private static void CheckScores(ReviewScores scores, List<ReviewViolation> found)
    {
        foreach (var (name, value) in scores.Scored())
        {
            if (value is < ReviewScores.Min or > ReviewScores.Max)
            {
                found.Add(new ReviewViolation(ScoreOutOfRange, "scores." + name, Repaired: false));
            }
        }
    }

    private static ReviewIssueV1 CheckIssue(
        ReviewIssueV1 issue,
        int index,
        int paragraphCount,
        HashSet<string> seenIds,
        List<ReviewViolation> found)
    {
        var path = $"issues[{index}]";

        if (string.IsNullOrWhiteSpace(issue.Id) || !seenIds.Add(issue.Id))
        {
            found.Add(new ReviewViolation(DuplicateIssueId, path + ".id", Repaired: false));
        }
        else if (!IsIssueId(issue.Id) || issue.Id.StartsWith(ReservedIdPrefix, StringComparison.Ordinal))
        {
            // CR-4/S2: the ear_ prefix is reserved for the issues the validator synthesises, so a model id can never collide with one.
            found.Add(new ReviewViolation(BadIssueId, path + ".id", Repaired: false));
        }

        var knownType = ReviewIssueTypes.TryParse(issue.Type, out var type);
        if (!knownType)
        {
            found.Add(new ReviewViolation(UnknownIssueType, path + ".type", Repaired: false));
        }

        var severity = issue.Severity;
        if (!string.Equals(severity, Blocking, StringComparison.Ordinal) && !string.Equals(severity, Advisory, StringComparison.Ordinal))
        {
            found.Add(new ReviewViolation(UnknownSeverity, path + ".severity", Repaired: false));
        }
        else if (knownType && severity == Blocking && !ReviewIssueTypes.IsBlockingAllowed(type))
        {
            found.Add(new ReviewViolation(TasteIssueBlocking, path + ".severity", Repaired: true));
            severity = Advisory;
        }

        CheckSegment(issue.Segment, paragraphCount, path + ".segment", found);
        CheckLength(issue.Evidence, path + ".evidence", found);
        CheckFieldLength(issue.Impact, path + ".impact", found);
        CheckFieldLength(issue.RepairDirection, path + ".repair_direction", found);

        return issue with { Severity = severity };
    }

    private static void CheckAnswers(
        IReadOnlyList<EarRuleAnswer> answers,
        IReadOnlyCollection<string> questionIds,
        int paragraphCount,
        List<ReviewViolation> found)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < answers.Count; index++)
        {
            var answer = answers[index];
            var path = $"ear_rule_answers[{index}]";

            if (!questionIds.Contains(answer.QuestionId) || !seen.Add(answer.QuestionId))
            {
                found.Add(new ReviewViolation(BadEarRuleAnswer, path + ".question_id", Repaired: false));
            }

            CheckSegment(answer.Segment, paragraphCount, path + ".segment", found);
            CheckLength(answer.Evidence, path + ".evidence", found);
        }

        // CR-1: every question that was asked must be answered.
        foreach (var questionId in questionIds)
        {
            if (!seen.Contains(questionId))
            {
                found.Add(new ReviewViolation(MissingEarRuleAnswer, $"ear_rule_answers[{questionId}]", Repaired: false));
            }
        }
    }

    private static void CheckClaims(IReadOnlyList<RealWorldClaim> claims, FactualCareReview? factual, int paragraphCount, List<ReviewViolation> found)
    {
        for (var index = 0; index < claims.Count; index++)
        {
            var claim = claims[index];
            CheckSegment(claim.Segment, paragraphCount, $"real_world_claims[{index}].segment", found);
            CheckLength(claim.Evidence, $"real_world_claims[{index}].evidence", found);
        }

        if (factual is not null)
        {
            for (var index = 0; index < factual.UnsupportedClaims.Count; index++)
            {
                var claim = factual.UnsupportedClaims[index];
                CheckSegment(claim.Segment, paragraphCount, $"factual_care.unsupported_claims[{index}].segment", found);
                CheckLength(claim.Claim, $"factual_care.unsupported_claims[{index}].claim", found);
            }
        }
    }

    private static void AddMissingEarRuleIssues(
        IReadOnlyList<EarRuleAnswer> answers,
        List<ReviewIssueV1> issues,
        List<ReviewViolation> found)
    {
        var earType = ReviewIssueTypes.Wire(ReviewIssueType.EarRuleViolation);
        for (var index = 0; index < answers.Count; index++)
        {
            var answer = answers[index];
            if (answer.Passed || !SegmentPattern().IsMatch(answer.Segment))
            {
                continue;
            }

            var covered = issues.Any(issue => issue.Type == earType && issue.Segment == answer.Segment);
            if (covered)
            {
                continue;
            }

            // The ear_ prefix is reserved (BadIssueId), so no model issue can already hold this id.
            var id = ReservedIdPrefix + answer.QuestionId.ToLowerInvariant();

            found.Add(new ReviewViolation(EarRuleFailureWithoutIssue, $"ear_rule_answers[{index}]", Repaired: true));
            issues.Add(new ReviewIssueV1(
                id,
                earType,
                Advisory,
                answer.Segment,
                answer.Evidence,
                "A sentence fails a naturalness question for narration written for the ear.",
                "Revise the quoted sentence so it passes " + answer.QuestionId + "."));
        }
    }

    private static void CheckSegment(string segment, int paragraphCount, string path, List<ReviewViolation> found)
    {
        if (!SegmentPattern().IsMatch(segment))
        {
            found.Add(new ReviewViolation(BadSegment, path, Repaired: false));
            return;
        }

        const string Prefix = "p:";
        if (segment.StartsWith(Prefix, StringComparison.Ordinal)
            && !(int.TryParse(segment.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number < paragraphCount))
        {
            found.Add(new ReviewViolation(BadSegment, path, Repaired: false));
        }
    }

    private static void CheckLength(string text, string path, List<ReviewViolation> found)
    {
        if (text.Length > ValidationIssue.MaxEvidenceChars)
        {
            found.Add(new ReviewViolation(EvidenceTooLong, path, Repaired: false));
        }
    }

    private static void CheckFieldLength(string text, string path, List<ReviewViolation> found)
    {
        if (text.Length > MaxFieldChars)
        {
            found.Add(new ReviewViolation(FieldTooLong, path, Repaired: false));
        }
    }

    private static void CheckCount(int count, int max, string path, List<ReviewViolation> found)
    {
        if (count > max)
        {
            found.Add(new ReviewViolation(TooManyItems, path, Repaired: false));
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(title|hook|ending|meta|p:\d+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
