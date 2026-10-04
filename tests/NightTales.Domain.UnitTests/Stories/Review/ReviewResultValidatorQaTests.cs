using NightTales.Domain.Series;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Review;

/// <summary>
/// 02.6.1-T1 QA: negative and boundary cases around the closed 17-type list, the score, segment and evidence bounds and the
/// <c>factual_care</c> rules that the implementer's tests only touch once each.
/// </summary>
public sealed class ReviewResultValidatorQaTests
{
    private const int Paragraphs = 5;
    private static readonly string[] Questions = ["Q1", "Q2", "Q3", "Q4", "Q5", "Q6"];

    private static ReviewScores Scores(int? factual = null, int hook = 4) => new(hook, 3, 4, 4, 3, factual, 4);

    private static ReviewIssueV1 Issue(string type = "missing_setup_before_twist", string severity = "advisory", string segment = "p:3", string evidence = "x", string id = "r1") =>
        new(id, type, severity, segment, evidence, "impact", "repair");

    private static StoryReviewV1 Review(
        ReviewIssueV1[]? issues = null,
        ReviewScores? scores = null,
        FactualCareReview? factual = null,
        EarRuleAnswer[]? answers = null,
        RealWorldClaim[]? claims = null) =>
        new(scores ?? Scores(), issues ?? [], answers ?? [], factual, claims ?? [], "ok");

    private static ValidatedReview Validate(StoryReviewV1 review, Topic topic = Topic.Ghost) =>
        ReviewResultValidator.Validate(review, topic, Paragraphs, Questions);

    private static string[] Rules(ValidatedReview result) => [.. result.Violations.Select(violation => violation.RuleId)];

    [Fact]
    public void ReviewIssueTypes_TheClosedListHasExactlySeventeenDistinctWireNames()
    {
        var names = Enum.GetValues<ReviewIssueType>().Select(ReviewIssueTypes.Wire).ToArray();

        names.Length.ShouldBe(17);
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(17);
        names.ShouldAllBe(name => name.All(character => char.IsAsciiLetterLower(character) || character == '_'));
        names.ShouldNotContain(ReviewIssueTypes.FactualCareMissing);
    }

    [Fact]
    public void ReviewResultValidator_EveryOneOfTheSeventeenTypes_IsAccepted()
    {
        foreach (var type in Enum.GetValues<ReviewIssueType>())
        {
            var wire = ReviewIssueTypes.Wire(type);
            var severity = ReviewIssueTypes.IsBlockingAllowed(type) ? "blocking" : "advisory";

            var result = Validate(Review([Issue(wire, severity)]));

            result.IsAccepted.ShouldBeTrue(wire);
            result.Violations.ShouldBeEmpty(wire);
        }
    }

    [Theory]
    [InlineData("plot_hole")]
    [InlineData("factual_care_missing")] // server-only type: the model may never return it
    [InlineData("Weak_Hook")]
    [InlineData("WEAK_HOOK")]
    [InlineData(" weak_hook")]
    [InlineData("weak_hook ")]
    [InlineData("weak-hook")]
    [InlineData("weak_hook\n")]
    [InlineData("")]
    [InlineData("tone_violation; drop table")]
    public void ReviewResultValidator_TypeOutsideTheClosedList_IsRejected(string type)
    {
        var result = Validate(Review([Issue(type)]));

        result.IsAccepted.ShouldBeFalse();
        Rules(result).ShouldBe([ReviewResultValidator.UnknownIssueType]);
        result.Rejections.Single().Path.ShouldBe("issues[0].type");
        result.Rejections.Single().Repaired.ShouldBeFalse();
    }

    [Fact]
    public void ReviewResultValidator_EachUnknownTypeIsReportedOnceAtItsOwnIndex()
    {
        var result = Validate(Review([
            Issue("plot_hole", id: "a"),
            Issue("weak_hook", id: "b"),
            Issue("pacing_issue", id: "c"),
            Issue("made_up", id: "d"),
        ]));

        result.IsAccepted.ShouldBeFalse();
        result.Rejections.Select(violation => violation.Path).ShouldBe(["issues[0].type", "issues[3].type"]);
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("Blocking")]
    [InlineData("ADVISORY")]
    [InlineData("")]
    public void ReviewResultValidator_UnknownSeverity_IsRejected(string severity)
    {
        var result = Validate(Review([Issue(severity: severity)]));

        result.IsAccepted.ShouldBeFalse();
        Rules(result).ShouldBe([ReviewResultValidator.UnknownSeverity]);
    }

    [Fact]
    public void ReviewResultValidator_UnknownTypeWithBlocking_ReportsTheTypeOnly()
    {
        // An unknown type cannot be judged for "blocking allowed"; it must not also trip the taste downgrade.
        var result = Validate(Review([Issue("plot_hole", "blocking")]));

        Rules(result).ShouldBe([ReviewResultValidator.UnknownIssueType]);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, false)]
    public void ReviewResultValidator_ScoreBounds(int hook, bool accepted)
    {
        var result = Validate(Review(scores: Scores(hook: hook)));

        result.IsAccepted.ShouldBe(accepted);
        if (!accepted)
        {
            result.Rejections.Single().Path.ShouldBe("scores.hook");
        }
    }

    [Fact]
    public void ReviewResultValidator_TrueCrimeFactualCareScoreOutOfRange_IsRejected()
    {
        var factual = new FactualCareReview(2, [], "n");

        var result = Validate(Review(scores: Scores(factual: 6), factual: factual), Topic.TrueCrime);

        result.IsAccepted.ShouldBeFalse();
        result.Rejections.Single().Path.ShouldBe("scores.factual_care");
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    [InlineData(0, true)]
    public void ReviewResultValidator_EvidenceLengthBoundary_AppliesToIssuesAnswersAndClaims(int length, bool accepted)
    {
        var text = new string('a', length);

        Validate(Review([Issue(evidence: text)])).IsAccepted.ShouldBe(accepted);
        Validate(Review(answers: [new EarRuleAnswer("Q1", true, "p:0", text)])).IsAccepted.ShouldBe(accepted);
        Validate(Review(claims: [new RealWorldClaim("p:0", text)])).IsAccepted.ShouldBe(accepted);
    }

    [Fact]
    public void ReviewResultValidator_FactualCareClaimTextOver200Chars_IsRejected()
    {
        var factual = new FactualCareReview(1, [new UnsupportedClaim("p:1", new string('c', 201))], "n");

        var result = Validate(Review(scores: Scores(factual: 3), factual: factual), Topic.TrueCrime);

        result.IsAccepted.ShouldBeFalse();
        result.Rejections.Single().Path.ShouldBe("factual_care.unsupported_claims[0].claim");
    }

    [Theory]
    [InlineData("title", true)]
    [InlineData("hook", true)]
    [InlineData("ending", true)]
    [InlineData("meta", true)]
    [InlineData("p:0", true)]
    [InlineData("p:4", true)] // the last of five paragraphs
    [InlineData("p:5", false)]
    [InlineData("p:", false)]
    [InlineData("p:1\n", false)]
    [InlineData("p:1 ", false)]
    [InlineData("p:+1", false)]
    [InlineData("p:99999999999999999999", false)]
    [InlineData("Title", false)]
    [InlineData("hook,ending", false)]
    [InlineData("p:٣", false)] // an Arabic-Indic digit is not a paragraph index
    public void ReviewResultValidator_SegmentBoundary(string segment, bool accepted)
    {
        Validate(Review([Issue(segment: segment)])).IsAccepted.ShouldBe(accepted);
        Validate(Review(claims: [new RealWorldClaim(segment, "x")])).IsAccepted.ShouldBe(accepted);
    }

    [Fact]
    public void ReviewResultValidator_EmptyOrWhitespaceIssueId_IsRejected()
    {
        Validate(Review([Issue(id: "")])).Rejections.Single().RuleId.ShouldBe(ReviewResultValidator.DuplicateIssueId);
        Validate(Review([Issue(id: "  ")])).Rejections.Single().RuleId.ShouldBe(ReviewResultValidator.DuplicateIssueId);
    }

    [Fact]
    public void ReviewResultValidator_QuestionIdOutsideTheAskedSet_OrRepeated_IsRejected()
    {
        var unknown = Validate(Review(answers: [new EarRuleAnswer("Q9", true, "p:0", "x")]));
        var repeated = Validate(Review(answers: [new EarRuleAnswer("Q1", true, "p:0", "x"), new EarRuleAnswer("Q1", true, "p:1", "y")]));

        Rules(unknown).ShouldBe([ReviewResultValidator.BadEarRuleAnswer]);
        Rules(repeated).ShouldBe([ReviewResultValidator.BadEarRuleAnswer]);
    }

    [Fact]
    public void ReviewResultValidator_FactualCareNullVersusPresent_PerTopic()
    {
        var factual = new FactualCareReview(2, [], "n");

        // Fiction: null/absent is the contract; a score alone or the object alone is a repaired slip (cleared, never fatal).
        Validate(Review()).Violations.ShouldBeEmpty();
        var scoreOnly = Validate(Review(scores: Scores(factual: 3)));
        var objectOnly = Validate(Review(factual: factual));
        foreach (var result in new[] { scoreOnly, objectOnly })
        {
            result.IsAccepted.ShouldBeTrue();
            Rules(result).ShouldBe([ReviewResultValidator.FactualCareForFiction]);
            result.Review.FactualCare.ShouldBeNull();
            result.Review.Scores.FactualCare.ShouldBeNull();
        }

        // True crime: both must be present; either one missing adds exactly one advisory server-side issue.
        Validate(Review(scores: Scores(factual: 3), factual: factual), Topic.TrueCrime).Violations.ShouldBeEmpty();
        foreach (var half in new[] { Review(scores: Scores(factual: 3)), Review(factual: factual), Review() })
        {
            var result = Validate(half, Topic.TrueCrime);

            result.IsAccepted.ShouldBeTrue();
            Rules(result).ShouldBe([ReviewResultValidator.FactualCareMissing]);
            var added = result.Review.Issues.ShouldHaveSingleItem();
            added.Type.ShouldBe(ReviewIssueTypes.FactualCareMissing);
            added.Severity.ShouldBe("advisory");
        }
    }

    [Fact]
    public void ReviewResultValidator_SpiritualAndGhost_AreBothFiction()
    {
        var factual = new FactualCareReview(1, [], "n");

        foreach (var topic in new[] { Topic.Ghost, Topic.Spiritual })
        {
            Rules(Validate(Review(factual: factual), topic)).ShouldBe([ReviewResultValidator.FactualCareForFiction]);
        }
    }

    [Fact]
    public void ReviewResultValidator_DoesNotMutateItsInput()
    {
        var original = Review(scores: Scores(factual: 3), factual: new FactualCareReview(1, [], "n"));

        _ = Validate(original);

        original.FactualCare.ShouldNotBeNull();
        original.Scores.FactualCare.ShouldBe(3);
    }
}
