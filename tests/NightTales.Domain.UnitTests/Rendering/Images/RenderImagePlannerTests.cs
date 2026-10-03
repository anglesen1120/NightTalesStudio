using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Stories.Keywords;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Rendering.Images;

/// <summary>
/// <c>RenderImagePlanner</c> (03 README D3, F03, F17; 03.2.1-T1): needed images from the narration length, the capped
/// term count with free spares, the deterministic trim order and the hints - with the two worked examples of 02 T103.
/// </summary>
public sealed class RenderImagePlannerTests
{
    private static readonly ImagePlanOptions Defaults = ImagePlanOptions.Defaults;

    [Theory]
    [InlineData(170, 6)]
    [InlineData(200, 6)]
    [InlineData(201, 7)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public void Plan_ClipTen_NeededIsCeilSecondsOverTen(int tokens, int expectedNeeded)
    {
        var plan = RenderImagePlanner.Plan(Input(Segments(40, 1), tokens, Language.En, LengthTier.Standard));

        plan.NeededImages.ShouldBe(expectedNeeded);
        plan.ClipSeconds.ShouldBe(10);
    }

    [Fact]
    public void Plan_ClipTwenty_HalvesTheNeededImages()
    {
        var options = Defaults with { ClipSeconds = 20 };

        var plan = RenderImagePlanner.Plan(Input(Segments(40, 1), 170, Language.En, LengthTier.Standard, options));

        plan.NeededImages.ShouldBe(3, "ceil(51 s / 20)");
        plan.MaxTerms.ShouldBe(4, "ceil(3 * 1.15), tier cap ceil(480 / 20) = 24");
    }

    [Fact]
    public void Plan_SpareShareAddsFifteenPercent()
    {
        var input = Input(Segments(60, 1), 1_560, Language.Vi, LengthTier.Standard);

        RenderImagePlanner.Plan(input).MaxTerms.ShouldBe(42, "ceil(36 * 1.15) = ceil(41.4)");
        RenderImagePlanner.Plan(input with { Options = Defaults with { SpareShare = 0 } }).MaxTerms.ShouldBe(36);
    }

    [Fact]
    public void Plan_TierCapBinds_ForLongTextInShortTier()
    {
        var plan = RenderImagePlanner.Plan(Input(Segments(30, 1), 400, Language.En, LengthTier.Short));

        plan.NeededImages.ShouldBe(12);
        plan.MaxTerms.ShouldBe(6, "a Short episode never exceeds ceil(1 * 60 / 10)");
        plan.Terms.Count.ShouldBe(6);
    }

    [Theory]
    [InlineData(LengthTier.Short, 6)]
    [InlineData(LengthTier.Standard, 48)]
    [InlineData(LengthTier.Long, 90)]
    public void Plan_TierCaps_AreSixFortyEightNinety(LengthTier tier, int cap)
    {
        var plan = RenderImagePlanner.Plan(Input(Segments(100, 1), 100_000, Language.En, tier));

        plan.MaxTerms.ShouldBe(cap);
    }

    [Fact]
    public void Plan_StandardVi_1560Tokens_Needs36_Max42_NoTrim()
    {
        // 26 segments, 13 with two keywords and 13 with one: 39 keywords.
        var segments = Enumerable.Range(0, 26)
            .Select(i => new SegmentKeywords($"p:{i}", i % 2 == 0 ? [$"a{i}", $"b{i}"] : [$"a{i}"]))
            .ToList();

        var plan = RenderImagePlanner.Plan(Input(segments, 1_560, Language.Vi, LengthTier.Standard));

        plan.NeededImages.ShouldBe(36);
        plan.MaxTerms.ShouldBe(42);
        plan.Terms.Count.ShouldBe(39);
        plan.TrimmedCount.ShouldBe(0);
        plan.HintCodes.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_ShortEn_170Tokens_TrimsSecondKeywordsFromTheEnd()
    {
        var segments = Enumerable.Range(0, 5).Select(i => new SegmentKeywords($"p:{i}", [$"a{i}", $"b{i}"])).ToList();

        var plan = RenderImagePlanner.Plan(Input(segments, 170, Language.En, LengthTier.Short));

        plan.NeededImages.ShouldBe(6);
        plan.MaxTerms.ShouldBe(6);
        plan.TrimmedCount.ShouldBe(4);
        plan.Terms.Select(t => t.Keyword).ShouldBe(["a0", "b0", "a1", "a2", "a3", "a4"]);
        plan.HintCodes.ShouldBe([RenderImagePlanner.ScenesTrimmed]);
    }

    [Fact]
    public void Plan_TrimOrder_SecondKeywordsBeforeFirstKeywords()
    {
        var segments = Enumerable.Range(0, 20).Select(i => new SegmentKeywords($"p:{i}", [$"a{i}", $"b{i}"])).ToList();

        // 860 en tokens = 258 s -> needed 26 -> cap ceil(29.9) = 30.
        var capped30 = RenderImagePlanner.Plan(Input(segments, 860, Language.En, LengthTier.Standard));
        capped30.MaxTerms.ShouldBe(30);
        var expected30 = Enumerable.Range(0, 20).SelectMany(i => i < 10 ? new[] { $"a{i}", $"b{i}" } : [$"a{i}"]).ToList();
        capped30.Terms.Select(t => t.Keyword).ShouldBe(expected30, "all 20 first keywords and the second keywords of segments 0-9");
        capped30.TrimmedCount.ShouldBe(10);

        // 430 en tokens = 129 s -> needed 13 -> cap ceil(14.95) = 15.
        var capped15 = RenderImagePlanner.Plan(Input(segments, 430, Language.En, LengthTier.Standard));
        capped15.MaxTerms.ShouldBe(15);
        capped15.Terms.Select(t => t.Keyword).ShouldBe(Enumerable.Range(0, 15).Select(i => $"a{i}").ToList(),
            "every second keyword goes, then the first keywords of segments 19..15");
        capped15.TrimmedCount.ShouldBe(25);
    }

    [Fact]
    public void Plan_FirstKeywordsAloneExceedCap_DropsFromTheEnd()
    {
        // 30 single-keyword segments, cap 15 (430 tokens, Standard).
        var plan = RenderImagePlanner.Plan(Input(Segments(30, 1), 430, Language.En, LengthTier.Standard));

        plan.MaxTerms.ShouldBe(15);
        plan.Terms.Select(t => t.Segment).ShouldBe(Enumerable.Range(0, 15).Select(i => $"p:{i}").ToList());
        plan.Terms.Select(t => t.Slot).ShouldBe(Enumerable.Range(0, 15).ToList());
        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesTrimmed);
    }

    [Fact]
    public void Plan_FewKeywords_ScenesSparseHint()
    {
        // needed 36 -> sparse line ceil(36 * 0.7) = 26.
        RenderImagePlanner.Plan(Input(Segments(3, 1), 1_560, Language.Vi, LengthTier.Standard))
            .HintCodes.ShouldBe([RenderImagePlanner.ScenesSparse]);
        RenderImagePlanner.Plan(Input(Segments(25, 1), 1_560, Language.Vi, LengthTier.Standard))
            .HintCodes.ShouldBe([RenderImagePlanner.ScenesSparse]);
        RenderImagePlanner.Plan(Input(Segments(26, 1), 1_560, Language.Vi, LengthTier.Standard))
            .HintCodes.ShouldBeEmpty("exactly at the sparse line is not sparse");
    }

    [Fact]
    public void Plan_SlotsFollowStoryOrder_WithSegmentAddresses()
    {
        var segments = new List<SegmentKeywords>
        {
            new("hook", ["old door", "dark hall"]),
            new("p:0", ["lantern light"]),
            new("p:1", ["empty chair", "cold window"]),
            new("ending", ["morning fog"]),
        };

        var plan = RenderImagePlanner.Plan(Input(segments, 1_560, Language.Vi, LengthTier.Standard));

        plan.Terms.ShouldBe(
        [
            new PlannedTerm(0, "hook", "old door"),
            new PlannedTerm(1, "hook", "dark hall"),
            new PlannedTerm(2, "p:0", "lantern light"),
            new PlannedTerm(3, "p:1", "empty chair"),
            new PlannedTerm(4, "p:1", "cold window"),
            new PlannedTerm(5, "ending", "morning fog"),
        ]);
    }

    [Fact]
    public void Plan_ZeroKeywords_EmptyTermsAndSparseHint()
    {
        var plan = RenderImagePlanner.Plan(Input([], 1_560, Language.Vi, LengthTier.Standard));

        plan.Terms.ShouldBeEmpty();
        plan.TrimmedCount.ShouldBe(0);
        plan.NeededImages.ShouldBe(36);
        plan.HintCodes.ShouldBe([RenderImagePlanner.ScenesSparse]);
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var segments = Enumerable.Range(0, 20).Select(i => new SegmentKeywords($"p:{i}", [$"a{i}", $"b{i}"])).ToList();

        var first = RenderImagePlanner.Plan(Input(segments, 430, Language.En, LengthTier.Standard));
        var second = RenderImagePlanner.Plan(Input(segments, 430, Language.En, LengthTier.Standard));

        second.ShouldBe(first);
        second.GetHashCode().ShouldBe(first.GetHashCode());
        RenderImagePlanner.Plan(Input(segments, 860, Language.En, LengthTier.Standard)).ShouldNotBe(first);
    }

    [Fact]
    public void Plan_NeverEditsKeywordText()
    {
        string[] keywords = ["  Old  Door ", "DARK hall", "lantern-light"];
        var segments = new List<SegmentKeywords> { new("hook", keywords), new("p:0", ["x y z"]) };

        var plan = RenderImagePlanner.Plan(Input(segments, 1_560, Language.Vi, LengthTier.Standard));

        plan.Terms.Select(t => t.Keyword).ShouldBe(["  Old  Door ", "DARK hall", "lantern-light", "x y z"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Plan_NonPositiveClip_Throws(int clip)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RenderImagePlanner.Plan(Input(Segments(3, 1), 100, Language.En, LengthTier.Short, Defaults with { ClipSeconds = clip })));
    }

    private static List<SegmentKeywords> Segments(int count, int perSegment) =>
        Enumerable.Range(0, count)
            .Select(i => new SegmentKeywords($"p:{i}", Enumerable.Range(0, perSegment).Select(k => $"kw{i}_{k}").ToList()))
            .ToList();

    private static RenderImagePlanInput Input(
        IReadOnlyList<SegmentKeywords> segments,
        int tokens,
        Language language,
        LengthTier tier,
        ImagePlanOptions? options = null) =>
        new(segments, tokens, language, tier, options ?? Defaults);
}
