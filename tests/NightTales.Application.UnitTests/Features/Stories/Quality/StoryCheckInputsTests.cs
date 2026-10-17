using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Quality;

/// <summary>
/// The plan the edit and generation callers pass into the checker (03.2.2-T2, 03 CR-14): its hint codes are what
/// makes <c>scenes_trimmed</c> and <c>scenes_sparse</c> reach <c>audit_report.local.issues</c>.
/// </summary>
public sealed class StoryCheckInputsTests
{
    // 60 vi syllables: at the D3 default rates (260/min, 10 s clips) that is 2 needed images, so the Short
    // tier's spare share allows 3 terms and its sparse line is 2.
    private static readonly string[] Words = [.. Enumerable.Repeat("canh", 60)];

    [Fact]
    public void PlanFor_NoLengthTier_ReturnsNull()
    {
        var plan = StoryCheckInputs.PlanFor(
            Draft(hookKeywords: 1, bodyKeywords: 1, endingKeywords: 1),
            Language.Vi,
            tier: null);

        plan.ShouldBeNull("Q-TIER-01 already reports the missing tier; there is no plan to build.");
    }

    [Fact]
    public void PlanFor_ShortStoryWithTenKeywords_RaisesScenesTrimmed()
    {
        // 10 terms against a cap of 3: the planner drops 7, so the edit shows the trimmed hint.
        var plan = StoryCheckInputs.PlanFor(
                Draft(hookKeywords: 2, bodyKeywords: 6, endingKeywords: 2),
                Language.Vi,
                LengthTier.Short)
            .ShouldNotBeNull();

        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesTrimmed);
        plan.TrimmedCount.ShouldBe(7);
        plan.HintCodes.ShouldNotContain(RenderImagePlanner.ScenesSparse);
    }

    [Fact]
    public void PlanFor_FewKeywords_RaisesScenesSparse()
    {
        // One term against a sparse line of 2 needed images: too few scenes, but nothing trimmed.
        var plan = StoryCheckInputs.PlanFor(
                Draft(hookKeywords: 1, bodyKeywords: 0, endingKeywords: 0),
                Language.Vi,
                LengthTier.Short)
            .ShouldNotBeNull();

        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesSparse);
        plan.HintCodes.ShouldNotContain(RenderImagePlanner.ScenesTrimmed);
        plan.Terms.ShouldHaveSingleItem().Segment.ShouldBe(SegmentAddress.Hook);
    }

    [Fact]
    public void PlanFor_PartialDraft_ReturnsPlanInsteadOfThrowing()
    {
        // An edited draft can arrive with a missing segment or paragraph list; deriving the body and the
        // segment list tolerates both, so the caller never needs a try/catch around the plan.
        var partial = new StoryContentV1(
            "title",
            Hook: null!,
            Paragraphs: null!,
            Ending: null!,
            Description: "description",
            Tags: [],
            ContentWarnings: [],
            Structure: null!);

        var plan = StoryCheckInputs.PlanFor(partial, Language.En, LengthTier.Standard).ShouldNotBeNull();

        plan.Terms.ShouldBeEmpty();
        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesSparse);
    }

    [Fact]
    public void PlanFor_TrimmedHint_ReachesTheStoredAuditIssuesAsAdvisory()
    {
        // AC4's chain below the wire: the edit path plans the draft, the checker turns the plan's hint code into an
        // issue (Q-SCENE-01), and the D22 projection the handler persists stores it as an advisory row of
        // audit_report.local.issues (EditStoryCommandHandler.BuildAuditReport serialises exactly this projection).
        var draft = Draft(hookKeywords: 2, bodyKeywords: 6, endingKeywords: 2);
        var plan = StoryCheckInputs.PlanFor(draft, Language.Vi, LengthTier.Short).ShouldNotBeNull();
        plan.HintCodes.ShouldContain(RenderImagePlanner.ScenesTrimmed);

        var rules = new StoryRules();
        var report = new StoryQualityChecker().Check(
            draft,
            new StoryCheckContext(
                Topic.Ghost,
                LengthTier.Short,
                rules.Length,
                rules,
                0,
                [],
                false,
                null,
                SimilarityVerdict.Pass,
                null,
                false,
                plan,
                NameCorpus.Empty));

        var hint = report.ToAuditIssues().Single(issue => issue.Rule == StoryQualityChecker.ScenesTrimmed);
        hint.Severity.ShouldBe("advisory");
        hint.Kind.ShouldBe("objective");
        hint.Segment.ShouldBe("meta");
    }

    /// <summary>A 60-token draft with the given keyword count in each of its three segments.</summary>
    private static StoryContentV1 Draft(int hookKeywords, int bodyKeywords, int endingKeywords) => new(
        "The story",
        new StorySegmentV1(Text(20), Keywords("hook", hookKeywords), []),
        [new StorySegmentV1(Text(20), Keywords("body", bodyKeywords), [])],
        new StorySegmentV1(Text(20), Keywords("ending", endingKeywords), []),
        "A description.",
        [],
        [],
        new StoryStructureV1("ghost story", "old house", "a midnight call", "the narrator", "first_person"));

    private static string Text(int count) => string.Join(' ', Words.Take(count));

    private static List<string> Keywords(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(index => $"{prefix} keyword {index}")];
}
