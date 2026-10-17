using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The pick of 02.8.1 plan §3.4 (task Acceptance 2, Tests <c>IdeaScorer_*</c>): the weighted score with
/// the 0…1 clamp, the neutral values a degraded judge call leaves behind, and the tie rule that keeps the
/// winner deterministic.
/// </summary>
public sealed class IdeaScorerTests
{
    /// <summary>A fresher and better-fitting idea beats a familiar one, and wins the pick.</summary>
    [Fact]
    public void IdeaScorer_HigherNoveltyAndFit_Wins()
    {
        // w.Novelty × (1 − 0.1) + w.Fit × 0.9 + w.Hook × 0.8 = 0.405 + 0.270 + 0.200
        var fresh = new IdeaSignals(0.1m, 0.1m, 0.9m, 0.8m, 0m);
        // w.Novelty × (1 − 0.5) + w.Fit × 0.5 + w.Hook × 0.5 = 0.225 + 0.150 + 0.125
        var familiar = new IdeaSignals(0.5m, 0.5m, 0.5m, 0.5m, 0m);
        var weights = new IdeaWeights();

        var freshScore = IdeaScorer.Score(fresh, weights);
        var familiarScore = IdeaScorer.Score(familiar, weights);

        freshScore.ShouldBe(0.875m);
        familiarScore.ShouldBe(0.5m);
        freshScore.ShouldBeGreaterThan(familiarScore);
        IdeaScorer.PickIndex([familiarScore, freshScore]).ShouldBe(1);
    }

    /// <summary>Equal scores pick the lowest index, so a batch's winner never depends on iteration order.</summary>
    [Fact]
    public void IdeaScorer_TieBreaksByIndex()
    {
        var score = IdeaScorer.Score(new IdeaSignals(0.2m, 0.2m, 0.8m, 0.6m, 0m), new IdeaWeights());

        IdeaScorer.PickIndex([score, score]).ShouldBe(0);
        IdeaScorer.PickIndex([null, score, score]).ShouldBe(1);
    }

    /// <summary>Over-weighting configuration cannot push the score out of the column's 0…1 domain.</summary>
    [Fact]
    public void IdeaScorer_ClampsToZeroOne()
    {
        var generous = new IdeaWeights(Novelty: 1m, Fit: 1m, Hook: 1m, Variety: 0m);
        var punishing = new IdeaWeights(Novelty: 0m, Fit: 0m, Hook: 0m, Variety: 1m);

        IdeaScorer.Score(new IdeaSignals(0m, 0m, 1m, 1m, 0m), generous).ShouldBe(1m);
        IdeaScorer.Score(new IdeaSignals(0m, 0m, 1m, 1m, 1m), punishing).ShouldBe(0m);
    }

    /// <summary>A degraded judge call scores on the lexical score alone, with the plan's neutral values.</summary>
    [Fact]
    public void IdeaScorer_LexicalOnly_IsTheNeutralDegradation()
    {
        var signals = IdeaSignals.LexicalOnly(lexicalScore: 0.3m, varietyPenalty: 0.2m);

        signals.LexicalScore.ShouldBe(0.3m);
        signals.JudgeScore.ShouldBe(0m);
        signals.JudgeFit.ShouldBe(1m, "an idea the judge never saw is not punished for fit");
        signals.JudgeHookStrength.ShouldBe(0.5m);
        signals.VarietyPenalty.ShouldBe(0.2m);

        // w.Novelty × (1 − 0.3) + w.Fit × 1 + w.Hook × 0.5 − w.Variety × 0.2 = 0.315 + 0.300 + 0.125 − 0.070
        IdeaScorer.Score(signals, new IdeaWeights()).ShouldBe(0.67m);
    }

    /// <summary>Ineligible candidates carry no score and can never win; no eligible candidate means no pick.</summary>
    [Fact]
    public void IdeaScorer_PickIndex_IgnoresIneligibleCandidates()
    {
        IdeaScorer.PickIndex([]).ShouldBeNull();
        IdeaScorer.PickIndex([null]).ShouldBeNull();
        IdeaScorer.PickIndex([null, null]).ShouldBeNull();
        IdeaScorer.PickIndex([null, 0.4m]).ShouldBe(1);
        IdeaScorer.PickIndex([0.4m, null]).ShouldBe(0);
        IdeaScorer.PickIndex([0.4m, null, 0.4m]).ShouldBe(0);
        IdeaScorer.PickIndex([0m, null]).ShouldBe(0, "a zero score still wins when it is the only candidate left");
    }
}
