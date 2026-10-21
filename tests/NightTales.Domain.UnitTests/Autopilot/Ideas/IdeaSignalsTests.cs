using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The scoring inputs of 02.8.1 plan §3.4 that 02.8.1-T1 ships (<see cref="IdeaSignals"/> and
/// <see cref="IdeaWeights"/>): the configurable defaults and the clamp that keeps the score inside the
/// column's 0…1 domain whatever the weights are.
/// </summary>
public sealed class IdeaSignalsTests
{
    [Fact]
    public void IdeaWeights_Defaults_AreThePlanValues()
    {
        var weights = new IdeaWeights();

        weights.Novelty.ShouldBe(0.45m);
        weights.Fit.ShouldBe(0.30m);
        weights.Hook.ShouldBe(0.25m);
        weights.Variety.ShouldBe(0.35m);
    }

    [Fact]
    public void IdeaSignals_DefaultValues_AreTheNeutralDegradation()
    {
        // Plan §4 U2: when the judge call degrades, the scorer uses noveltyComponent = 1 − lexical,
        // Fit = 1.0 and HookStrength = 0.5, so those are the neutral values a caller can state explicitly.
        var signals = new IdeaSignals(LexicalScore: 0.4m, JudgeScore: 0m, JudgeFit: 1m, JudgeHookStrength: 0.5m, VarietyPenalty: 0m);

        signals.LexicalScore.ShouldBe(0.4m);
        signals.JudgeScore.ShouldBe(0m);
        signals.JudgeFit.ShouldBe(1m);
        signals.JudgeHookStrength.ShouldBe(0.5m);
        signals.VarietyPenalty.ShouldBe(0m);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(-0.0001, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1, 1)]
    [InlineData(1.0001, 1)]
    [InlineData(2.5, 1)]
    public void IdeaSignals_Clamp01_LimitsToTheScoreDomain(double value, double expected) =>
        IdeaSignals.Clamp01((decimal)value).ShouldBe((decimal)expected);
}
