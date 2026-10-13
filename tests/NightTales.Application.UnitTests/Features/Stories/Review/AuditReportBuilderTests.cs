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

    // ---- 02.6.2-T1 MergeRevision ----

    private static readonly Guid Parent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ValidatedReview ReviewAfter(params ReviewIssueV1[] issues) =>
        new(
            new StoryReviewV1(
                new ReviewScores(4, 3, 4, 4, 3, null, 4),
                issues,
                [],
                null,
                [],
                "summary after the revision"),
            []);

    private static JsonObject MergeRevision(string existing, RevisionDrift drift, params ReviewIssueV1[] issues) =>
        JsonNode.Parse(AuditReportBuilder.MergeRevision(existing, Parent, ["r1", "r2"], ReviewAfter(issues), drift))!.AsObject();

    [Fact]
    public void AuditReportBuilder_MergeRevision_SetsParentAndFixedIssueIds()
    {
        var root = MergeRevision(Existing, RevisionDrift.None, new ReviewIssueV1("r3", "missing_setup_before_twist", "blocking", "p:3", "e", "i", "r"));

        var revision = root["revision"]!.AsObject();
        revision.Select(pair => pair.Key).ShouldBe(["applied", "fixed_issue_ids", "parent_version_id", "drift", "review_after"], ignoreOrder: true);
        revision["applied"]!.GetValue<bool>().ShouldBeTrue();
        revision["fixed_issue_ids"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["r1", "r2"]);
        revision["parent_version_id"]!.GetValue<string>().ShouldBe(Parent.ToString("D"));
        revision["review_after"]!["scores"]!.AsObject().Select(pair => pair.Key).ShouldBe(ReviewScores.Dimensions, ignoreOrder: true);

        // Every other key of the stored document is carried over untouched.
        root["local"]!.ToJsonString().ShouldBe("""{"issues":[]}""");
        root["moderation"]!.ToJsonString().ShouldBe("""{"flagged":false,"categories":[]}""");
        root["model_review"]!.ToJsonString().ShouldBe("""{"stale":true,"old":1}""");
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_DriftIsAdvisoryOnly()
    {
        var drift = new RevisionDrift(SegmentCountChanged: false, ChangedSegments: ["p:3", "ending"], AddedFindings: ["hook_missing"]);

        var root = MergeRevision(Existing, drift, new ReviewIssueV1("r9", "weak_hook", "advisory", "hook", "e", "i", "r"));

        var issue = root["revision"]!["review_after"]!["issues"]!.AsArray()
            .Single(node => node!["type"] is { } type && type.GetValue<string>() == AuditReportBuilder.RevisionDriftType)!.AsObject();

        issue["severity"]!.GetValue<string>().ShouldBe("advisory");
        issue["kind"]!.GetValue<string>().ShouldBe("objective");
        issue["segment"]!.GetValue<string>().ShouldBe("p:3");
        issue.ContainsKey("type").ShouldBeTrue();

        // Advisory only: the drift never presents as blocking.
        root["revision"]!["review_after"]!["issues"]!.AsArray()
            .Select(node => node!["severity"]!.GetValue<string>()).ShouldNotContain("blocking");

        var detail = root["revision"]!["drift"]!.AsObject();
        detail["segment_count_changed"]!.GetValue<bool>().ShouldBeFalse();
        detail["changed_segments"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["p:3", "ending"]);
        detail["added_findings"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["hook_missing"]);
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_NoDrift_WritesEmptyDetailAndNoDriftIssue()
    {
        var root = MergeRevision(Existing, RevisionDrift.None, new ReviewIssueV1("r9", "weak_hook", "advisory", "hook", "e", "i", "r"));

        root["revision"]!["drift"]!.ToJsonString()
            .ShouldBe("""{"segment_count_changed":false,"changed_segments":[],"added_findings":[]}""");
        root["revision"]!["review_after"]!["issues"]!.AsArray().ShouldHaveSingleItem();
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_CleansAndCapsModelStrings()
    {
        var hostile = new ReviewIssueV1(
            "r1\u0000x",
            "weak_hook",
            "advisory",
            "hook",
            "a\u0000b\u0001c",
            new string('x', 500),
            "ok");
        var drift = new RevisionDrift(SegmentCountChanged: false, ChangedSegments: [new string('s', 200)], AddedFindings: [new string('f', 200)]);

        var root = MergeRevision(Existing, drift, hostile);

        var issue = root["revision"]!["review_after"]!["issues"]![0]!;
        issue["evidence"]!.GetValue<string>().ShouldBe("a b c");
        issue["impact"]!.GetValue<string>().Length.ShouldBe(ReviewResultValidator.MaxFieldChars);
        root["revision"]!["fixed_issue_ids"]![0]!.GetValue<string>().ShouldBe("r1 x");
        root["revision"]!["drift"]!["changed_segments"]![0]!.GetValue<string>().Length.ShouldBe(AuditReportBuilder.RevisionIdMaxChars);
        root["revision"]!["drift"]!["added_findings"]![0]!.GetValue<string>().Length.ShouldBe(AuditReportBuilder.RevisionIdMaxChars);
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_DriftIssueEvidenceIsCleanedAndCapped()
    {
        var huge = Enumerable.Range(0, 40).Select(index => $"p:{index}").ToArray();
        var drift = new RevisionDrift(SegmentCountChanged: true, ChangedSegments: huge, AddedFindings: []);

        var root = MergeRevision(Existing, drift, new ReviewIssueV1("r9", "weak_hook", "advisory", "hook", "e", "i", "r"));
        var issue = root["revision"]!["review_after"]!["issues"]!.AsArray()
            .Single(node => node!["type"] is { } type && type.GetValue<string>() == AuditReportBuilder.RevisionDriftType)!.AsObject();

        issue["evidence"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(200);
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_IsIdempotent()
    {
        var drift = new RevisionDrift(SegmentCountChanged: false, ChangedSegments: ["p:1"], AddedFindings: []);
        var review = ReviewAfter(new ReviewIssueV1("r3", "weak_hook", "advisory", "hook", "e", "i", "r")).Review;

        var once = AuditReportBuilder.MergeRevision(Existing, Parent, ["r1"], new ValidatedReview(review, []), drift);
        var twice = AuditReportBuilder.MergeRevision(once, Parent, ["r1"], new ValidatedReview(review, []), drift);

        twice.ShouldBe(once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AuditReportBuilder_MergeRevision_NoStoredDocument_CreatesAnAuditV1WithOnlyRevision(string stored)
    {
        var root = JsonNode.Parse(AuditReportBuilder.MergeRevision(stored, Parent, [], ReviewAfter(), RevisionDrift.None))!.AsObject();

        root.Select(pair => pair.Key).ShouldBe(["schema_version", "revision"], ignoreOrder: true);
        root["schema_version"]!.GetValue<string>().ShouldBe("audit.v1");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void AuditReportBuilder_MergeRevision_StoredDocumentNotAnObject_Throws(string stored)
    {
        Should.Throw<System.Text.Json.JsonException>(() => AuditReportBuilder.MergeRevision(stored, Parent, [], ReviewAfter(), RevisionDrift.None));
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_RejectedReviewAfter_Throws()
    {
        var rejected = new ValidatedReview(ReviewAfter().Review, [new ReviewViolation(ReviewResultValidator.BadSegment, "issues[0].segment", Repaired: false)]);

        var error = Should.Throw<InvalidOperationException>(() => AuditReportBuilder.MergeRevision(Existing, Parent, [], rejected, RevisionDrift.None));
        error.Message.ShouldContain(ReviewResultValidator.BadSegment);
    }

    [Fact]
    public void AuditReportBuilder_MergeRevision_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => AuditReportBuilder.MergeRevision(Existing, Parent, null!, ReviewAfter(), RevisionDrift.None));
        Should.Throw<ArgumentNullException>(() => AuditReportBuilder.MergeRevision(Existing, Parent, [], null!, RevisionDrift.None));
        Should.Throw<ArgumentNullException>(() => AuditReportBuilder.MergeRevision(Existing, Parent, [], ReviewAfter(), null!));
    }

    [Fact]
    public void AuditReportBuilder_MergeRevisionNotApplied_WritesAppliedFalseAndReason()
    {
        var root = JsonNode.Parse(AuditReportBuilder.MergeRevisionNotApplied(Existing, AuditReportBuilder.RevisionReasonSpendCapReached))!.AsObject();

        root["revision"]!.ToJsonString().ShouldBe("""{"applied":false,"reason":"spend_cap_reached"}""");
        root["model_review"]!.ToJsonString().ShouldBe("""{"stale":true,"old":1}""");

        var invalid = JsonNode.Parse(AuditReportBuilder.MergeRevisionNotApplied(Existing, AuditReportBuilder.RevisionReasonInvalidOutput))!.AsObject();
        invalid["revision"]!["applied"]!.GetValue<bool>().ShouldBeFalse();
        invalid["revision"]!["reason"]!.GetValue<string>().ShouldBe("revision_invalid_output");
    }

    [Theory]
    [InlineData("free text")]
    [InlineData("spend cap reached")]
    [InlineData(" ")]
    public void AuditReportBuilder_MergeRevisionNotApplied_ReasonOutsideTheStableCodeSet_Throws(string reason)
    {
        Should.Throw<ArgumentException>(() => AuditReportBuilder.MergeRevisionNotApplied(Existing, reason));
    }

    [Fact]
    public void AuditReportBuilder_MergeRevisionNotApplied_IsIdempotent()
    {
        var once = AuditReportBuilder.MergeRevisionNotApplied(Existing, AuditReportBuilder.RevisionReasonInvalidOutput);
        var twice = AuditReportBuilder.MergeRevisionNotApplied(once, AuditReportBuilder.RevisionReasonInvalidOutput);

        twice.ShouldBe(once);
    }
}
