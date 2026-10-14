using NightTales.TestKit.Review;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Review;

/// <summary>
/// 02.6.1-T2 (D15): the pure seeded-model decision over small hand-built reports, so every branch of the rule is
/// checked without the API — luna wins when eligible, sol is the fallback, a partial pass or a null report is never
/// eligible, and the two thresholds are inclusive exactly at their boundary.
/// </summary>
public sealed class SeededModelDecisionTests
{
    [Fact]
    public void SeededModelDecision_LunaPassesBothPicksLunaEvenIfSolPasses()
    {
        var luna = new SeededModelPass(Report(recall: 0.92, falseBlockRate: 0.0));
        var sol = new SeededModelPass(Report(recall: 1.0, falseBlockRate: 0.0));

        var decision = SeededModelDecision.Decide(luna, sol);

        decision.Choice.ShouldBe(SeededModelChoice.Luna);
        decision.Luna.Eligible.ShouldBeTrue();
        decision.Sol.Eligible.ShouldBeTrue();
        decision.Luna.Recall.ShouldBe(0.92);
        decision.Sol.FalseBlockRate.ShouldBe(0.0);
    }

    [Fact]
    public void SeededModelDecision_SolPicked_WhenOnlyLunaFailsFalseBlock()
    {
        var luna = new SeededModelPass(Report(recall: 0.95, falseBlockRate: 0.4));
        var sol = new SeededModelPass(Report(recall: 0.85, falseBlockRate: 0.0));

        var decision = SeededModelDecision.Decide(luna, sol);

        decision.Choice.ShouldBe(SeededModelChoice.Sol);
        decision.Luna.Eligible.ShouldBeFalse();
        decision.Sol.Eligible.ShouldBeTrue();
    }

    [Fact]
    public void SeededModelDecision_NoneWhenNeitherPasses()
    {
        var luna = new SeededModelPass(Report(recall: 0.5, falseBlockRate: 0.0));
        var sol = new SeededModelPass(Report(recall: 0.9, falseBlockRate: 0.5));

        var decision = SeededModelDecision.Decide(luna, sol);

        decision.Choice.ShouldBe(SeededModelChoice.None);
        decision.Luna.Eligible.ShouldBeFalse();
        decision.Sol.Eligible.ShouldBeFalse();
    }

    [Fact]
    public void SeededModelDecision_PassingRecallButFailingFalseBlockIsNotEligible()
    {
        var pass = new SeededModelPass(Report(recall: 0.9, falseBlockRate: 0.2));

        SeededModelDecision.IsEligible(pass).ShouldBeFalse();
        SeededModelDecision.Decide(pass, pass).Choice.ShouldBe(SeededModelChoice.None);
    }

    [Fact]
    public void SeededModelDecision_PartialReportNeverEligible()
    {
        var perfectButPartial = new SeededModelPass(Report(recall: 1.0, falseBlockRate: 0.0), Partial: true);
        var eligibleSol = new SeededModelPass(Report(recall: 0.85, falseBlockRate: 0.0));

        SeededModelDecision.IsEligible(perfectButPartial).ShouldBeFalse();
        SeededModelDecision.Decide(perfectButPartial, eligibleSol).Choice.ShouldBe(SeededModelChoice.Sol);

        var partialSol = new SeededModelPass(Report(recall: 1.0, falseBlockRate: 0.0), Partial: true);
        SeededModelDecision.Decide(perfectButPartial, partialSol).Choice.ShouldBe(SeededModelChoice.None);

        // A pass that scored nothing (null report) is never eligible, partial or not.
        SeededModelDecision.IsEligible(new SeededModelPass(Report: null)).ShouldBeFalse();
        SeededModelDecision.IsEligible(new SeededModelPass(Report: null, Partial: true)).ShouldBeFalse();
    }

    [Fact]
    public void SeededModelDecision_BoundaryThresholdsAreEligible()
    {
        // Exactly at both gates: recall 0.80 (>= is inclusive) and false-block 0.10 (<= is inclusive).
        var boundary = new SeededModelPass(Report(recall: SeededModelDecision.RecallThreshold, falseBlockRate: SeededModelDecision.FalseBlockThreshold));
        SeededModelDecision.IsEligible(boundary).ShouldBeTrue();

        // One step below recall fails; one step above false-blocking fails.
        SeededModelDecision.IsEligible(new SeededModelPass(Report(recall: 0.7999, falseBlockRate: 0.10))).ShouldBeFalse();
        SeededModelDecision.IsEligible(new SeededModelPass(Report(recall: 0.80, falseBlockRate: 0.1001))).ShouldBeFalse();
    }

    [Fact]
    public void SeededModelDecision_NullPassThrows()
    {
        Should.Throw<ArgumentNullException>(() => SeededModelDecision.Decide(null!, new SeededModelPass(Report(1.0, 0.0))));
    }

    /// <summary>A hand-built report; only <paramref name="recall"/> and <paramref name="falseBlockRate"/> drive the decision.</summary>
    private static SeededDefectReport Report(double recall, double falseBlockRate) =>
        new(
            Drafts: 22,
            CleanDrafts: 5,
            SeededDefects: 26,
            DetectedDefects: (int)Math.Round(recall * 26, MidpointRounding.AwayFromZero),
            Recall: recall,
            CleanDraftsWithBlocking: (int)Math.Round(falseBlockRate * 5, MidpointRounding.AwayFromZero),
            FalseBlockRate: falseBlockRate,
            RecallByType: new Dictionary<string, double>(StringComparer.Ordinal),
            UnseededBlockingTypes: [],
            CostUsd: 1.234567m);
}
