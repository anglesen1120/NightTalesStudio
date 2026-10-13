using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Review;

/// <summary>
/// The segment-drift check of a single revision pass (02.6.2-T1, D21 step 5): an addressed segment may change,
/// an untouched one may not, and a missing segment, paragraph list or beat is always drift.
/// </summary>
public sealed class SegmentDriftDetectorTests
{
    private const double Similarity = 0.6;

    [Fact]
    public void SegmentDriftDetector_SegmentCountChanged_ReportsDrift()
    {
        var original = Story("the opening line", ["first body", "second body"], "the closing line");
        var revised = Story("the opening line", ["first body"], "the closing line");

        var drift = Detect(original, revised);

        drift.SegmentCountChanged.ShouldBeTrue();
        drift.HasDrift.ShouldBeTrue();
        drift.ChangedSegments.ShouldBe(["p:1", "ending"]);
    }

    [Fact]
    public void SegmentDriftDetector_AddressedSegmentRewritten_NoDrift()
    {
        var original = Story("the opening line", ["first body", "the quiet house on the hill"], "the closing line");
        var revised = Story("the opening line", ["first body", "completely different words now here"], "the closing line");

        var drift = SegmentDriftDetector.Detect(original, revised, ["p:1"], Similarity, [], [], Language.Vi);

        drift.HasDrift.ShouldBeFalse();
        drift.SegmentCountChanged.ShouldBeFalse();
        drift.ChangedSegments.ShouldBeEmpty();
        drift.AddedFindings.ShouldBeEmpty();
    }

    [Fact]
    public void SegmentDriftDetector_UntouchedSegmentLowSimilarity_ReportsDrift()
    {
        var original = Story("the opening line", ["first body", "the quiet house on the hill"], "the closing line");
        var revised = Story("the opening line", ["first body", "completely different words now here"], "the closing line");

        var drift = Detect(original, revised);

        drift.HasDrift.ShouldBeTrue();
        drift.SegmentCountChanged.ShouldBeFalse();
        drift.ChangedSegments.ShouldBe(["p:1"]);
    }

    [Fact]
    public void SegmentDriftDetector_UntouchedSegmentHighSimilarity_NoDrift()
    {
        var original = Story("the opening line", ["the quiet house on the hill"], "the closing line");
        var revised = Story("the opening line", ["the quiet house on the hill"], "the closing line");

        Detect(original, revised).HasDrift.ShouldBeFalse();
    }

    [Fact]
    public void SegmentDriftDetector_BeatChangedOnUntouchedSegment_ReportsDrift()
    {
        var original = Story("the opening line", ["first body"], "the closing line", beats: ["setup"]);
        var revised = Story("the opening line", ["first body"], "the closing line", beats: ["twist"]);

        var drift = Detect(original, revised);

        drift.ChangedSegments.ShouldBe(["p:0"]);
        drift.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public void SegmentDriftDetector_NewBlockingLocalFinding_AddedFinding()
    {
        var original = Story("the opening line", ["first body"], "the closing line");
        var revised = Story("the opening line", ["first body"], "the closing line");

        var drift = SegmentDriftDetector.Detect(
            original, revised, [], Similarity,
            originalBlockingRules: ["length_out_of_range", "hook_missing"],
            revisedBlockingRules: ["length_out_of_range", "hook_missing", "ear_rule_violation", "ear_rule_violation"],
            Language.Vi);

        drift.AddedFindings.ShouldBe(["ear_rule_violation"]);
        drift.HasDrift.ShouldBeTrue();
        drift.SegmentCountChanged.ShouldBeFalse();
        drift.ChangedSegments.ShouldBeEmpty();
    }

    [Fact]
    public void SegmentDriftDetector_NoNewRule_NoAddedFinding()
    {
        var original = Story("the opening line", ["first body"], "the closing line");
        var revised = Story("the opening line", ["first body"], "the closing line");

        SegmentDriftDetector.Detect(original, revised, [], Similarity, ["hook_missing"], ["hook_missing"], Language.Vi)
            .AddedFindings.ShouldBeEmpty();
    }

    [Fact]
    public void SegmentDriftDetector_NullParagraphList_FailsClosed()
    {
        var original = Story("the opening line", ["first body", "second body"], "the closing line");
        var revised = original with { Paragraphs = null! };

        var drift = Detect(original, revised);

        drift.SegmentCountChanged.ShouldBeTrue();
        drift.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public void SegmentDriftDetector_NullBeatOnUntouchedParagraph_ReportsDrift()
    {
        var original = Story("the opening line", ["first body"], "the closing line", beats: ["setup"]);
        var revised = Story("the opening line", ["first body"], "the closing line", beats: [null]);

        Detect(original, revised).ChangedSegments.ShouldBe(["p:0"]);
    }

    [Fact]
    public void SegmentDriftDetector_NullSegment_FailsClosed()
    {
        var original = Story("the opening line", ["first body"], "the closing line");
        var revised = original with { Hook = null! };

        var drift = Detect(original, revised);

        drift.HasDrift.ShouldBeTrue();
        drift.ChangedSegments.ShouldBe(["hook"]);
    }

    [Fact]
    public void SegmentDriftDetector_InvalidThreshold_FailsClosed()
    {
        var original = Story("the opening line", ["the quiet house on the hill"], "the closing line");
        var revised = Story("the opening line", ["a totally different thing here"], "the closing line");

        // A NaN threshold would fail open under `<`; it is clamped to 1, so a changed segment still drifts.
        Detect(original, revised, double.NaN).ChangedSegments.ShouldBe(["p:0"]);
    }

    [Fact]
    public void SegmentDriftDetector_LoneSurrogateInText_DoesNotThrowAndDetectsTheChange()
    {
        var original = Story("the opening line", ["quiet house \uD800 broken"], "the closing line");
        var revised = Story("the opening line", ["a different world here now"], "the closing line");

        Detect(original, revised).ChangedSegments.ShouldBe(["p:0"]);
    }

    [Fact]
    public void SegmentDriftDetector_NullInputs_Throw()
    {
        var story = Story("hook", ["body"], "ending");

        Should.Throw<ArgumentNullException>(() => SegmentDriftDetector.Detect(null!, story, [], Similarity, [], [], Language.Vi));
        Should.Throw<ArgumentNullException>(() => SegmentDriftDetector.Detect(story, null!, [], Similarity, [], [], Language.Vi));
        Should.Throw<ArgumentNullException>(() => SegmentDriftDetector.Detect(story, story, null!, Similarity, [], [], Language.Vi));
        Should.Throw<ArgumentNullException>(() => SegmentDriftDetector.Detect(story, story, [], Similarity, null!, [], Language.Vi));
        Should.Throw<ArgumentNullException>(() => SegmentDriftDetector.Detect(story, story, [], Similarity, [], null!, Language.Vi));
    }

    private static RevisionDrift Detect(StoryContentV1 original, StoryContentV1 revised) =>
        SegmentDriftDetector.Detect(original, revised, [], Similarity, [], [], Language.Vi);

    private static RevisionDrift Detect(StoryContentV1 original, StoryContentV1 revised, double minSimilarity) =>
        SegmentDriftDetector.Detect(original, revised, [], minSimilarity, [], [], Language.Vi);

    private static StoryContentV1 Story(string hook, string[] paragraphs, string ending, string?[]? beats = null)
    {
        var body = new List<StorySegmentV1>(paragraphs.Length);
        for (var index = 0; index < paragraphs.Length; index++)
        {
            body.Add(new StorySegmentV1(paragraphs[index], ["keyword"], [], beats is null ? "setup" : beats[index]));
        }

        return new StoryContentV1(
            "A quiet night",
            new StorySegmentV1(hook, ["keyword"], []),
            body,
            new StorySegmentV1(ending, ["keyword"], []),
            "A short story.",
            ["ghost"],
            [],
            new StoryStructureV1("sub", "setting", "opening", "twist", "first_person"));
    }
}
