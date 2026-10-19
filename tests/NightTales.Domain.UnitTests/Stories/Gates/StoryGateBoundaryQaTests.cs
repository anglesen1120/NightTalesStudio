using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Review;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// QA boundary tests for the story gate (02.8.2-T1): inclusive thresholds at their exact value, one step
/// beyond them, and the empty allow-list case. Written by the QA agent; they complement
/// <see cref="StoryGateRulesTests"/> and do not replace it.
/// </summary>
public sealed class StoryGateBoundaryQaTests
{
    [Fact]
    public void QA_StoryGate_MeanExactlyAtFloor_Passes()
    {
        // Six scored dimensions summing to 21: a mean of exactly 3.5, the inclusive floor.
        var scores = new ReviewScores(Hook: 3, Pacing: 4, Atmosphere: 4, NaturalLanguage: 3, Originality: 4, FactualCare: null, LengthFit: 3);

        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.Audit(scores: scores)),
            AutopilotGateProfile.Defaults);

        var mean = GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore);
        mean.Pass.ShouldBeTrue();
        mean.Value.ShouldBe((decimal?)3.5m);
        mean.Threshold.ShouldBe((decimal?)3.5m);
    }

    [Fact]
    public void QA_StoryGate_MeanOneStepBelowFloor_Fails()
    {
        // Six dimensions summing to 20: a mean of 3.33…, just under the 3.5 floor.
        var scores = new ReviewScores(Hook: 3, Pacing: 3, Atmosphere: 4, NaturalLanguage: 4, Originality: 3, FactualCare: null, LengthFit: 3);

        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.Audit(scores: scores)),
            AutopilotGateProfile.Defaults);

        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore).Pass.ShouldBeFalse();
        result.Failures.ShouldBe([StoryGateCheckIds.SelfReviewMeanScore]);
    }

    [Fact]
    public void QA_StoryGate_LowestDimensionExactlyAtFloor_Passes()
    {
        // One dimension at 3 (the inclusive floor), the rest at 4: the mean passes and so does the minimum.
        var scores = new ReviewScores(Hook: 3, Pacing: 4, Atmosphere: 4, NaturalLanguage: 4, Originality: 4, FactualCare: null, LengthFit: 4);

        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.Audit(scores: scores)),
            AutopilotGateProfile.Defaults);

        var minimum = GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMinDimension);
        minimum.Pass.ShouldBeTrue();
        minimum.Value.ShouldBe((decimal?)3m);
        minimum.Threshold.ShouldBe((decimal?)3.0m);
        result.Pass.ShouldBeTrue();
    }

    [Fact]
    public void QA_StoryGate_LowestDimensionBelowFloor_FailsEvenWhenMeanPasses()
    {
        // One dimension at 2 drags nothing else: the mean is 3.67 (passes) but the minimum 2 fails its own check.
        var scores = new ReviewScores(Hook: 2, Pacing: 4, Atmosphere: 4, NaturalLanguage: 4, Originality: 4, FactualCare: null, LengthFit: 4);

        var result = StoryGateRules.Evaluate(
            GateTestData.StoryInput(audit: GateTestData.Audit(scores: scores)),
            AutopilotGateProfile.Defaults);

        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMeanScore).Pass.ShouldBeTrue();
        GateTestData.Find(result.Checks, StoryGateCheckIds.SelfReviewMinDimension).Pass.ShouldBeFalse();
        result.Failures.ShouldBe([StoryGateCheckIds.SelfReviewMinDimension]);
    }

    [Fact]
    public void QA_StoryGate_SimilarityExactlyAtCaps_Passes_AndOneStepBeyondFails()
    {
        var atCaps = GateTestData.StoryInput(
            similarity: new SimilaritySignals(Sources: 0.05m, PastEpisodes: 0.10m, CorpusMax: 50));
        var atCapsResult = StoryGateRules.Evaluate(atCaps, AutopilotGateProfile.Defaults);

        atCapsResult.Pass.ShouldBeTrue();
        GateTestData.Find(atCapsResult.Checks, StoryGateCheckIds.SimilaritySources).Pass.ShouldBeTrue();
        GateTestData.Find(atCapsResult.Checks, StoryGateCheckIds.SimilarityPastEpisodes).Pass.ShouldBeTrue();

        var beyond = GateTestData.StoryInput(
            similarity: new SimilaritySignals(Sources: 0.0501m, PastEpisodes: 0.10m, CorpusMax: 50));
        var beyondResult = StoryGateRules.Evaluate(beyond, AutopilotGateProfile.Defaults);

        beyondResult.Pass.ShouldBeFalse();
        beyondResult.Failures.ShouldBe([StoryGateCheckIds.SimilaritySources]);
    }

    [Fact]
    public void QA_StoryGate_EmptyAllowList_AcceptsNoWarningsAndRejectsAny()
    {
        // An empty allow-list allows no warning, but a story that declares none has nothing to violate.
        var noAllowList = AutopilotGateProfile.Defaults with
        {
            Story = StoryGateThresholds.Defaults with { AllowedContentWarnings = [] },
        };

        var none = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(contentWarnings: [])),
            noAllowList);
        GateTestData.Find(none.Checks, StoryGateCheckIds.ContentWarningsAllowed).Pass.ShouldBeTrue();

        var one = StoryGateRules.Evaluate(
            GateTestData.StoryInput(version: GateTestData.Version(contentWarnings: ["violence"])),
            noAllowList);
        GateTestData.Find(one.Checks, StoryGateCheckIds.ContentWarningsAllowed).Pass.ShouldBeFalse();
    }
}
