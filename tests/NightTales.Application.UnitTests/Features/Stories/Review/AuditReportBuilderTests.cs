using System.Text.Json.Nodes;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Review;

/// <summary>The merge of a review into <c>audit.v1.model_review</c> (02 README D22, 02.6.1-T1).</summary>
public sealed class AuditReportBuilderTests
{
    private const string Existing = """
        {"schema_version":"audit.v1","local":{"issues":[]},"moderation":{"flagged":false,"categories":[]},"model_review":{"stale":true,"old":1}}
        """;

    private static ValidatedReview Review(params ReviewIssueV1[] issues) =>
        new(
            new StoryReviewV1(
                new ReviewScores(4, 3, 4, 4, 3, null, 4),
                issues,
                [new EarRuleAnswer("Q1", Passed: false, "p:2", "ý")],
                null,
                [new RealWorldClaim("p:1", "a quoted claim")],
                "summary"),
            []);

    private static JsonObject Merge(string existing, params ReviewIssueV1[] issues) =>
        JsonNode.Parse(AuditReportBuilder.MergeModelReview(existing, Review(issues), "gpt-6-luna", "rel=1;review=v1"))!.AsObject();

    [Fact]
    public void AuditReportBuilder_MergeModelReview_WritesD22KeysOnly()
    {
        var root = Merge(
            Existing,
            new ReviewIssueV1("r1", "missing_setup_before_twist", "blocking", "p:3", "evidence", "impact", "repair"));

        root.Select(pair => pair.Key).ShouldBe(["schema_version", "local", "moderation", "model_review"], ignoreOrder: true);
        root["local"]!.ToJsonString().ShouldBe("""{"issues":[]}""");
        root["moderation"]!.ToJsonString().ShouldBe("""{"flagged":false,"categories":[]}""");

        var review = root["model_review"]!.AsObject();
        review.Select(pair => pair.Key).ShouldBe(["model", "prompt_version", "stale", "scores", "issues", "real_world_claims", "ear_rule_answers"], ignoreOrder: true);
        review["model"]!.GetValue<string>().ShouldBe("gpt-6-luna");
        review["prompt_version"]!.GetValue<string>().ShouldBe("rel=1;review=v1");
        review["stale"]!.GetValue<bool>().ShouldBeFalse();
        review["scores"]!.AsObject().Select(pair => pair.Key).ShouldBe(ReviewScores.Dimensions, ignoreOrder: true);
        review["scores"]!["factual_care"].ShouldBeNull();

        var issue = review["issues"]!.AsArray().Single()!.AsObject();
        issue.Select(pair => pair.Key).ShouldBe(["id", "severity", "kind", "segment", "evidence", "impact", "repair_direction"], ignoreOrder: true);
        review["real_world_claims"]!.AsArray().Single()!.AsObject().Select(pair => pair.Key).ShouldBe(["segment", "evidence"], ignoreOrder: true);
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_DerivesKindFromType()
    {
        var root = Merge(
            string.Empty,
            new ReviewIssueV1("r1", "missing_setup_before_twist", "blocking", "p:3", "e", "i", "r"),
            new ReviewIssueV1("r2", "weak_hook", "advisory", "hook", "e", "i", "r"),
            new ReviewIssueV1("r3", ReviewIssueTypes.FactualCareMissing, "advisory", "meta", string.Empty, "i", "r"));

        root["schema_version"]!.GetValue<string>().ShouldBe("audit.v1");
        root["model_review"]!["issues"]!.AsArray().Select(node => node!["kind"]!.GetValue<string>()).ShouldBe(["objective", "taste", "objective"]);
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_ReplacesAPreviousBlockAndKeepsNonAsciiReadable()
    {
        var json = AuditReportBuilder.MergeModelReview(
            Existing,
            Review(new ReviewIssueV1("r1", "weak_hook", "advisory", "hook", "Ngôi nhà cũ", "i", "r")),
            "gpt-6-luna",
            "rel=1");

        json.ShouldContain("Ngôi nhà cũ");
        JsonNode.Parse(json)!["model_review"]!.AsObject().ContainsKey("old").ShouldBeFalse();
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_NotAnObject_Throws()
    {
        Should.Throw<System.Text.Json.JsonException>(() => AuditReportBuilder.MergeModelReview("[]", Review(), "m", "v"));
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_RejectedReview_Throws()
    {
        var rejected = new ValidatedReview(Review().Review, [new ReviewViolation(ReviewResultValidator.BadSegment, "issues[0].segment", Repaired: false)]);

        var error = Should.Throw<InvalidOperationException>(() => AuditReportBuilder.MergeModelReview(Existing, rejected, "gpt-6-luna", "rel=1"));
        error.Message.ShouldContain(ReviewResultValidator.BadSegment);
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_ReviewWithOnlyRepairedFindings_IsMerged()
    {
        var repaired = new ValidatedReview(Review().Review, [new ReviewViolation(ReviewResultValidator.TasteIssueBlocking, "issues[0].severity", Repaired: true)]);

        AuditReportBuilder.MergeModelReview(Existing, repaired, "gpt-6-luna", "rel=1").ShouldContain("model_review");
    }
}
