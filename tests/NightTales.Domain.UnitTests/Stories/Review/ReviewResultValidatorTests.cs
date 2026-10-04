using System.Text.Json;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Review;

/// <summary>The rules of 02.6.1-T1's failure table: what the validator rejects and what it repairs with a warning.</summary>
public sealed class ReviewResultValidatorTests
{
    private const int Paragraphs = 5;
    private static readonly string[] Questions = ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6"];

    private static ReviewScores Scores(int? factual = null) => new(4, 3, 4, 4, 3, factual, 4);

    private static ReviewIssueV1 Issue(string type = "missing_setup_before_twist", string severity = "advisory", string segment = "p:3", string evidence = "x", string id = "r1") =>
        new(id, type, severity, segment, evidence, "impact", "repair");

    private static StoryReviewV1 Review(
        ReviewIssueV1[]? issues = null,
        ReviewScores? scores = null,
        FactualCareReview? factual = null,
        EarRuleAnswer[]? answers = null) =>
        new(scores ?? Scores(), issues ?? [], answers ?? [], factual, [], "ok");

    private static IEnumerable<string> Rejected(ValidatedReview result) => result.Rejections.Select(violation => violation.RuleId);

    [Fact]
    public void ReviewResultValidator_ValidReview_IsAcceptedWithoutViolations()
    {
        var result = ReviewResultValidator.Validate(Review([Issue(severity: "blocking")]), Topic.Ghost, Paragraphs, Questions);

        result.IsAccepted.ShouldBeTrue();
        result.Violations.ShouldBeEmpty();
    }

    [Fact]
    public void ReviewResultValidator_UnknownIssueType_Fails()
    {
        var result = ReviewResultValidator.Validate(Review([Issue(type: "plot_hole")]), Topic.Ghost, Paragraphs, Questions);

        result.IsAccepted.ShouldBeFalse();
        Rejected(result).ShouldBe([ReviewResultValidator.UnknownIssueType]);
    }

    [Fact]
    public void ReviewResultValidator_TasteIssueMarkedBlocking_Fails()
    {
        // The failure table downgrades a blocking taste issue to advisory with a warning: a model slip never fails a run.
        var result = ReviewResultValidator.Validate(Review([Issue(type: "weak_hook", severity: "blocking")]), Topic.Ghost, Paragraphs, Questions);

        result.IsAccepted.ShouldBeTrue();
        result.Warnings.Select(violation => violation.RuleId).ShouldBe([ReviewResultValidator.TasteIssueBlocking]);
        result.Review.Issues.Single().Severity.ShouldBe("advisory");
    }

    [Theory]
    [InlineData("paragraph 3")]
    [InlineData("p:5")]
    [InlineData("p:-1")]
    [InlineData("P:1")]
    [InlineData("")]
    public void ReviewResultValidator_BadSegmentAddress_Fails(string segment)
    {
        var result = ReviewResultValidator.Validate(Review([Issue(segment: segment)]), Topic.Ghost, Paragraphs, Questions);

        Rejected(result).ShouldBe([ReviewResultValidator.BadSegment]);
    }

    [Fact]
    public void ReviewResultValidator_EvidenceOver200Chars_Fails()
    {
        var atBound = ReviewResultValidator.Validate(Review([Issue(evidence: new string('a', 200))]), Topic.Ghost, Paragraphs, Questions);
        var over = ReviewResultValidator.Validate(Review([Issue(evidence: new string('a', 201))]), Topic.Ghost, Paragraphs, Questions);

        atBound.IsAccepted.ShouldBeTrue();
        Rejected(over).ShouldBe([ReviewResultValidator.EvidenceTooLong]);
    }

    [Fact]
    public void ReviewResultValidator_FactualCareNonNullForFiction_Fails()
    {
        // Cleared to null with a warning (failure table): the stored document never carries factual care for fiction.
        var review = Review(scores: Scores(factual: 4), factual: new FactualCareReview(2, [], "n"));

        var result = ReviewResultValidator.Validate(review, Topic.Ghost, Paragraphs, Questions);

        result.Warnings.Select(violation => violation.RuleId).ShouldBe([ReviewResultValidator.FactualCareForFiction]);
        result.Review.Scores.FactualCare.ShouldBeNull();
        result.Review.FactualCare.ShouldBeNull();
    }

    [Fact]
    public void ReviewResultValidator_TrueCrimeWithoutFactualCare_AddsAnAdvisoryIssue()
    {
        var result = ReviewResultValidator.Validate(Review(), Topic.TrueCrime, Paragraphs, Questions);

        result.IsAccepted.ShouldBeTrue();
        result.Review.Issues.Single().ShouldSatisfyAllConditions(
            issue => issue.Type.ShouldBe(ReviewIssueTypes.FactualCareMissing),
            issue => issue.Severity.ShouldBe("advisory"));
    }

    [Fact]
    public void ReviewResultValidator_ScoreOutOfRangeOrDuplicateIssueId_Fails()
    {
        var scores = Scores() with { Hook = 6, LengthFit = 0 };

        var result = ReviewResultValidator.Validate(Review([Issue(), Issue()], scores), Topic.Ghost, Paragraphs, Questions);

        Rejected(result).ShouldBe([ReviewResultValidator.ScoreOutOfRange, ReviewResultValidator.ScoreOutOfRange, ReviewResultValidator.DuplicateIssueId]);
    }

    [Fact]
    public void ReviewResultValidator_FailedEarRuleAnswerWithoutIssue_AddsAnEarRuleViolationIssue()
    {
        var answers = new[] { new EarRuleAnswer("Q5", Passed: false, "p:2", "a clause") };

        var result = ReviewResultValidator.Validate(Review(answers: answers), Topic.Ghost, Paragraphs, Questions);

        result.IsAccepted.ShouldBeTrue();
        result.Review.Issues.Single().ShouldSatisfyAllConditions(
            issue => issue.Type.ShouldBe("ear_rule_violation"),
            issue => issue.Segment.ShouldBe("p:2"));
    }

    [Fact]
    public void ReviewScores_AllSevenDimensionsRequired()
    {
        ReviewScores.Dimensions.ShouldBe(["hook", "pacing", "atmosphere", "natural_language", "originality", "factual_care", "length_fit"]);
        Scores(factual: 3).Scored().Count().ShouldBe(7);
        Scores().Scored().Select(pair => pair.Name).ShouldNotContain("factual_care");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true };
        const string Complete = """{"hook":4,"pacing":3,"atmosphere":4,"natural_language":4,"originality":3,"factual_care":null,"length_fit":4}""";

        JsonSerializer.Deserialize<ReviewScores>(Complete, options).ShouldNotBeNull().FactualCare.ShouldBeNull();
        foreach (var dimension in ReviewScores.Dimensions)
        {
            var missing = JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, int?>>(Complete)!.Where(pair => pair.Key != dimension).ToDictionary());

            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ReviewScores>(missing, options), dimension);
        }
    }
}
