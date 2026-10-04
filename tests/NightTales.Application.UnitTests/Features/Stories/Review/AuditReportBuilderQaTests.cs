using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Review;

/// <summary>
/// 02.6.1-T1 QA: <see cref="AuditReportBuilder.MergeModelReview"/> must touch only <c>model_review</c> (02 README D22) and
/// leave every other key of the stored document - including ones this task has never heard of - exactly as stored.
/// </summary>
public sealed class AuditReportBuilderQaTests
{
    private const string Local = """{"issues":[{"rule":"length_out_of_range","severity":"blocking","kind":"objective","segment":"hook","evidence":"ý \"quoted\" <b>","repair_direction":"cut"}]}""";
    private const string Moderation = """{"flagged":true,"categories":["violence"],"checked_at":"2026-10-04T00:00:00Z"}""";
    private const string Revision = """{"applied":true,"fixed_issue_ids":["r1"],"parent_version_id":"11111111-1111-1111-1111-111111111111","drift":{"changed_segments":["p:3"],"unflagged_changed":[]}}""";
    private const string Similarity = """{"score":0.031,"n":10,"corpus":"sources","segments":[{"segment":"p:4","share":0.22}]}""";

    private static readonly string Stored =
        $$$"""{"schema_version":"audit.v1","local":{{{Local}}},"moderation":{{{Moderation}}},"model_review":{"stale":true,"old":1},"revision":{{{Revision}}},"similarity":{{{Similarity}}},"future_key":{"x":[1,2,3]}}""";

    private static ValidatedReview Review(string evidence = "e", string summary = "s", int? factual = null) =>
        new(
            new StoryReviewV1(
                new ReviewScores(4, 3, 4, 4, 3, factual, 4),
                [new ReviewIssueV1("r1", "weak_hook", "advisory", "hook", evidence, "impact", "repair")],
                [new EarRuleAnswer("Q1", true, "p:0", "ok")],
                null,
                [new RealWorldClaim("p:1", "a claim")],
                summary),
            []);

    private static JsonObject Merge(string stored, ValidatedReview? review = null) =>
        JsonNode.Parse(AuditReportBuilder.MergeModelReview(stored, review ?? Review(), "gpt-6-luna", "rel=1;review=v1"))!.AsObject();

    [Fact]
    public void AuditReportBuilder_MergeModelReview_PreservesLocalModerationAndEveryUnknownKeyVerbatim()
    {
        var root = Merge(Stored);

        root["local"]!.ToJsonString().ShouldBe(JsonNode.Parse(Local)!.ToJsonString());
        root["moderation"]!.ToJsonString().ShouldBe(JsonNode.Parse(Moderation)!.ToJsonString());
        root["revision"]!.ToJsonString().ShouldBe(JsonNode.Parse(Revision)!.ToJsonString());
        root["similarity"]!.ToJsonString().ShouldBe(JsonNode.Parse(Similarity)!.ToJsonString());
        root["future_key"]!.ToJsonString().ShouldBe("""{"x":[1,2,3]}""");
        root["schema_version"]!.GetValue<string>().ShouldBe("audit.v1");
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_ReplacesTheOldBlockInPlaceAndDropsItsStaleKeys()
    {
        var root = Merge(Stored);

        root.Select(pair => pair.Key).ShouldBe(["schema_version", "local", "moderation", "model_review", "revision", "similarity", "future_key"]);
        var review = root["model_review"]!.AsObject();
        review.ContainsKey("old").ShouldBeFalse();
        review["stale"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_IsIdempotent()
    {
        var once = AuditReportBuilder.MergeModelReview(Stored, Review(), "gpt-6-luna", "rel=1;review=v1");
        var twice = AuditReportBuilder.MergeModelReview(once, Review(), "gpt-6-luna", "rel=1;review=v1");

        twice.ShouldBe(once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AuditReportBuilder_MergeModelReview_NoStoredDocument_CreatesAnAuditV1WithOnlyModelReview(string stored)
    {
        var root = Merge(stored);

        root.Select(pair => pair.Key).ShouldBe(["schema_version", "model_review"]);
        root["schema_version"]!.GetValue<string>().ShouldBe("audit.v1");
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void AuditReportBuilder_MergeModelReview_StoredDocumentNotAnObject_Throws(string stored)
    {
        Should.Throw<JsonException>(() => AuditReportBuilder.MergeModelReview(stored, Review(), "gpt-6-luna", "rel=1"));
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_BlankModelOrPromptVersionOrNullReview_Throws()
    {
        Should.Throw<ArgumentException>(() => AuditReportBuilder.MergeModelReview(Stored, Review(), " ", "rel=1"));
        Should.Throw<ArgumentException>(() => AuditReportBuilder.MergeModelReview(Stored, Review(), "gpt-6-luna", string.Empty));
        Should.Throw<ArgumentNullException>(() => AuditReportBuilder.MergeModelReview(Stored, null!, "gpt-6-luna", "rel=1"));
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_ScoresAlwaysCarryAllSevenKeysAndFactualCareIsAnExplicitNull()
    {
        var scores = Merge(Stored)["model_review"]!["scores"]!.AsObject();

        scores.Select(pair => pair.Key).ShouldBe(["hook", "pacing", "atmosphere", "natural_language", "originality", "factual_care", "length_fit"]);
        scores.ContainsKey("factual_care").ShouldBeTrue();
        scores["factual_care"].ShouldBeNull();
        Merge(Stored, Review(factual: 3))["model_review"]!["scores"]!["factual_care"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_ModelTextThatLooksLikeJsonStaysAValue()
    {
        // Evidence is a quote of the draft, so it can hold anything: quotes, braces, a forged D22 key, markup.
        const string hostile = """"}],"moderation":{"flagged":false},"model_review":{"stale":true}} <script>x</script> \u0000 """";

        var json = AuditReportBuilder.MergeModelReview(Stored, Review(evidence: hostile, summary: hostile), "gpt-6-luna", "rel=1");
        var root = JsonNode.Parse(json)!.AsObject();

        root["moderation"]!.ToJsonString().ShouldBe(JsonNode.Parse(Moderation)!.ToJsonString());
        root["model_review"]!["stale"]!.GetValue<bool>().ShouldBeFalse();
        root["model_review"]!["issues"]![0]!["evidence"]!.GetValue<string>().ShouldBe(hostile);
        root.Select(pair => pair.Key).ShouldBe(["schema_version", "local", "moderation", "model_review", "revision", "similarity", "future_key"]);
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_KeepsNonAsciiReadableInTheStoredText()
    {
        var json = AuditReportBuilder.MergeModelReview(Stored, Review(evidence: "Ngôi nhà ma ám"), "gpt-6-luna", "rel=1");

        // Vietnamese text (new and carried over) stays literal; the quote is only re-escaped, its value is unchanged.
        json.ShouldContain("Ngôi nhà ma ám");
        json.ShouldContain("ý ");
        json.ShouldNotContain("\\u00FD");
        json.ShouldNotContain("\\u00F4");
        JsonNode.Parse(json)!["local"]!["issues"]![0]!["evidence"]!.GetValue<string>().ShouldBe("ý \"quoted\" <b>");
    }

    [Fact]
    public void AuditReportBuilder_MergeModelReview_IssueNeverCarriesATypeOrAModelChosenKind()
    {
        // D22 issues have id, severity, kind, segment, evidence, impact, repair_direction: the server derives kind from the type.
        var objective = new ReviewIssueV1("r1", "missing_setup_before_twist", "blocking", "p:1", "e", "i", "r");
        var taste = new ReviewIssueV1("r2", "weak_hook", "advisory", "hook", "e", "i", "r");
        var review = new ValidatedReview(Review().Review with { Issues = [objective, taste] }, []);

        var issues = Merge(Stored, review)["model_review"]!["issues"]!.AsArray();

        issues[0]!["kind"]!.GetValue<string>().ShouldBe("objective");
        issues[1]!["kind"]!.GetValue<string>().ShouldBe("taste");
        issues.ShouldAllBe(issue => !issue!.AsObject().ContainsKey("type"));
    }
}
