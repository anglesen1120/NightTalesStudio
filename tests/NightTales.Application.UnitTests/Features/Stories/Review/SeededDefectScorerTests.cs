using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;
using NightTales.TestKit.Review;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Review;

/// <summary>
/// 02.6.1-T2: <see cref="SeededDefectScorer"/> arithmetic and its detection rule, over small hand-built sets so every
/// number is computed by hand (no API, no fixtures, no mocking library).
/// </summary>
public sealed class SeededDefectScorerTests
{
    [Fact]
    public void SeededDefectScorer_DetectionRequiresTypeAndSegmentMatch()
    {
        var fixture = Seeded("d1", Defect("ear_rule_violation", "p:1"));
        var clean = CleanDraft("clean-1");

        var wrongSegment = Scored([fixture, clean], [Review(Issue("ear_rule_violation", "p:0")), Review()]);
        var wrongType = Scored([fixture, clean], [Review(Issue("tone_violation", "p:1")), Review()]);
        var both = Scored([fixture, clean], [Review(Issue("ear_rule_violation", "p:1")), Review()]);

        wrongSegment.DetectedDefects.ShouldBe(0);
        wrongType.DetectedDefects.ShouldBe(0);
        both.DetectedDefects.ShouldBe(1);
    }

    [Fact]
    public void SeededDefectScorer_RecallFromExpectedCounts()
    {
        // Five planted defects, four of them matched: recall is 0.8 exactly.
        var first = Seeded(
            "d1",
            Defect("ear_rule_violation", "p:0"),
            Defect("tone_violation", "p:1"),
            Defect("hook_missing", "hook"));
        var second = Seeded("d2", Defect("ending_missing", "ending"), Defect("beat_missing", "p:2"));
        var clean = CleanDraft("clean-1");

        var report = Scored(
            [first, second, clean],
            [
                Review(Issue("ear_rule_violation", "p:0"), Issue("tone_violation", "p:1"), Issue("hook_missing", "hook")),
                Review(Issue("ending_missing", "ending")),
                Review(),
            ]);

        report.Drafts.ShouldBe(3);
        report.SeededDefects.ShouldBe(5);
        report.DetectedDefects.ShouldBe(4);
        report.Recall.ShouldBe(0.8);
        report.RecallByType["ear_rule_violation"].ShouldBe(1.0);
        report.RecallByType["beat_missing"].ShouldBe(0.0);

        // Divide-by-zero and misalignment are deterministic errors, never NaN.
        var unseeded = CleanDraft("clean-2");
        Should.Throw<ArgumentException>(() => Scored([unseeded, clean], [Review(), Review()]));
        Should.Throw<ArgumentException>(() => Scored([first, clean], [Review()]));

        // The frozen set itself: a scripted perfect run reaches recall 1.0 with no false block.
        var real = SeededDraft.LoadAll().ToList();
        var perfect = real
            .Select(draft => Review([.. draft.SeedDefects.Select(defect => Issue(defect.Type, defect.Segment))]))
            .ToList();

        var perfectReport = Scored(real, perfect);

        perfectReport.Drafts.ShouldBe(22);
        perfectReport.CleanDrafts.ShouldBe(5);
        perfectReport.SeededDefects.ShouldBe(26);
        perfectReport.DetectedDefects.ShouldBe(26);
        perfectReport.Recall.ShouldBe(1.0);
        perfectReport.FalseBlockRate.ShouldBe(0.0);
        perfectReport.CleanDraftsWithBlocking.ShouldBe(0);
        perfectReport.UnseededBlockingTypes.ShouldBeEmpty();

        // A scripted run that misses exactly five planted defects scores 21/26.
        var toMiss = 5;
        var missing = real
            .Select(draft =>
            {
                var matched = new List<ReviewIssueV1>();
                foreach (var defect in draft.SeedDefects)
                {
                    if (toMiss > 0)
                    {
                        toMiss--;
                        continue;
                    }

                    matched.Add(Issue(defect.Type, defect.Segment));
                }

                return Review([.. matched]);
            })
            .ToList();

        var partialReport = Scored(real, missing);

        toMiss.ShouldBe(0);
        partialReport.SeededDefects.ShouldBe(26);
        partialReport.DetectedDefects.ShouldBe(21);
        partialReport.Recall.ShouldBe(21.0 / 26.0);
        partialReport.FalseBlockRate.ShouldBe(0.0);
    }

    [Fact]
    public void SeededDefectScorer_FalseBlockRateFromCleanDrafts()
    {
        // Five clean drafts, one of them blocked: the false-block rate is 0.2 exactly.
        var drafts = new List<SeededDraft> { Seeded("d1", Defect("ear_rule_violation", "p:0")) };
        var reviews = new List<StoryReviewV1> { Review(Issue("ear_rule_violation", "p:0")) };
        for (var i = 0; i < 5; i++)
        {
            drafts.Add(CleanDraft($"clean-{i}"));
            reviews.Add(i == 0 ? Review(Issue("tone_violation", "p:3")) : Review());
        }

        var report = Scored(drafts, reviews);

        report.CleanDrafts.ShouldBe(5);
        report.CleanDraftsWithBlocking.ShouldBe(1);
        report.FalseBlockRate.ShouldBe(0.2);

        // A set with no clean draft makes the false-block rate undefined: throw, never NaN.
        Should.Throw<ArgumentException>(() => Scored([Seeded("d1", Defect("ear_rule_violation", "p:0"))], [Review(Issue("ear_rule_violation", "p:0"))]));
    }

    [Fact]
    public void SeededDefectScorer_AdvisoryIssueStillCountsAsDetection()
    {
        // Recall measures detection, so an advisory issue of the same type and segment counts.
        var fixture = Seeded("d1", Defect("ear_rule_violation", "p:2"));
        var clean = CleanDraft("clean-1");

        var report = Scored(
            [fixture, clean],
            [Review(Issue("ear_rule_violation", "p:2", severity: "advisory")), Review()]);

        report.DetectedDefects.ShouldBe(1);
        report.Recall.ShouldBe(1.0);
    }

    [Fact]
    public void SeededDefectScorer_UnseededBlockingTypesReported()
    {
        // The list is the distinct blocking types on the clean drafts, in ordinal order; a blocking type on a seeded
        // draft that the draft does not plant is not part of the false-blocking signal.
        var fixture = Seeded("d1", Defect("ear_rule_violation", "p:0"));
        var clean = CleanDraft("clean-1");

        var report = Scored(
            [fixture, clean],
            [
                Review(Issue("ear_rule_violation", "p:0"), Issue("markdown_or_url_in_text", "p:5")),
                Review(Issue("tone_violation", "meta"), Issue("markdown_or_url_in_text", "p:1")),
            ]);

        report.UnseededBlockingTypes.ShouldBe(ExpectedUnseededBlockingTypes);
        report.CleanDraftsWithBlocking.ShouldBe(1);
    }

    private static readonly string[] ExpectedUnseededBlockingTypes = ["markdown_or_url_in_text", "tone_violation"];

    // ---- builders ---------------------------------------------------------------------------------------------------

    private static StorySegmentV1 Segment(string text, string? beat = null) => new(text, ["hall"], [], beat);

    private static StoryContentV1 Content() => new(
        "A title",
        Segment("hook"),
        [Segment("body one", "setup"), Segment("body two", "build_up")],
        Segment("ending"),
        "A description",
        ["ghost"],
        [],
        new StoryStructureV1("haunted_house", "a house", "cold open", "reveal", "first_person"));

    private static SeededDraft Seeded(string id, params SeededDefect[] defects) =>
        new(id, Language.En, Topic.Ghost, LengthTier.Standard, Clean: false, "brief", Content(), Defects: defects);

    private static SeededDraft CleanDraft(string id) =>
        new(id, Language.En, Topic.Ghost, LengthTier.Standard, Clean: true, "brief", Content(), Defects: []);

    private static SeededDefect Defect(string type, string segment) => new(type, segment, "hand-built defect");

    private static ReviewIssueV1 Issue(string type, string segment, string severity = "blocking") =>
        new("i1", type, severity, segment, "quote", "impact", "repair");

    private static StoryReviewV1 Review(params ReviewIssueV1[] issues) =>
        new(new ReviewScores(4, 4, 4, 4, 4, null, 4), issues, [], null, [], "summary");

    private static SeededDefectReport Scored(List<SeededDraft> drafts, List<StoryReviewV1> reviews) =>
        SeededDefectScorer.Score(drafts, reviews, costUsd: 0.123456m);
}
