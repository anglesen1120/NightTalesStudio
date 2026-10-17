using System.Globalization;
using System.Reflection;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity.Calibration;

/// <summary>
/// Boundary tests of the 02.5.6-T2 decision rule on small synthetic sample sets. Every synthetic story
/// has 100 distinct tokens and copies the first <c>c</c> tokens of its source, so its score at shingle size
/// <c>n</c> is exactly <c>(c - n + 1) / (101 - n)</c>. Every expected threshold below is derived from that formula.
/// The private <c>ChooseN</c>, <c>RoundUp</c> and <c>RoundDown</c> helpers are reached by reflection, because
/// their edges (the inclusive 0.9 candidate floor, the decimal rounding steps) cannot be hit with exact scores.
/// </summary>
[Trait("Category", "Calibration")]
public sealed class SimilarityCalibratorBoundaryTests
{
    private const int Length = 100;
    private const string Date = "2026-10-18";
    private static readonly string[] SpecificityClasses = ["original", "shared_collocation", "light_paraphrase", "quoted_passage"];
    private static readonly string[] StoryClasses =
    [
        "original", "shared_collocation", "light_paraphrase", "quoted_passage",
        "heavy_paraphrase", "verbatim_paragraph", "verbatim_full", "translated_copy",
    ];
    private static readonly int[] Grid = [6, 8, 10, 12, 14];
    private static readonly string SourceText = string.Join(' ', Seq("kq", 0, Length));

    [Theory]
    [InlineData(75.0, 150)]
    [InlineData(75.5, 151)]
    [InlineData(75.585, 152)]
    [InlineData(76.0, 152)]
    [InlineData(76.0000001, 153)]
    public void Calibration_Boundary_PerfBudget_IsCeilingOfTwiceP95(double p95, int expected)
    {
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: Length, specificityCopied: 0), Perf(p95), null, Date);

        report.Chosen.PerfBudgetMs.ShouldBe(expected);
    }

    [Fact]
    public void Calibration_Boundary_AllSpecificityZero_TiesChooseLargestN_WarnFloorAndMidpointBlock()
    {
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: Length, specificityCopied: 0), Perf(76.0), null, Date);

        report.Chosen.NVi.ShouldBe(14);
        report.Chosen.NEn.ShouldBe(14);
        report.Chosen.WarnAbove.ShouldBe(0.02, "WarnAbove is floored at 0.02");
        report.Chosen.BlockAbove.ShouldBe(0.5, "A = 0 and B = 1.0 give midpoint 0.50");
        report.Chosen.CopiedFactThreshold.ShouldBe(0.5);
        report.Warnings.ShouldAllBe(warning => warning.StartsWith("Expected dilution", StringComparison.Ordinal));
    }

    [Fact]
    public void Calibration_Boundary_NoCandidateN_KeepsDefaultShingleSizesAndWarns()
    {
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: 50, specificityCopied: 0), Perf(76.0), null, Date);

        report.Chosen.NVi.ShouldBe(10);
        report.Chosen.NEn.ShouldBe(8);
        report.Warnings.Any(warning => warning.Contains("'vi'", StringComparison.Ordinal)).ShouldBeTrue();
        report.Warnings.Any(warning => warning.Contains("'en'", StringComparison.Ordinal)).ShouldBeTrue();
    }

    /// <summary>
    /// Failure-handling row 1 (block side): with no qualifying n the defaults are kept. D1 sets WarnAbove 0.05 and
    /// BlockAbove 0.15 and never derives them from data.
    /// </summary>
    [Fact]
    public void Calibration_Boundary_NoCandidateN_KeepsDefaultWarnAbove005AndBlockAbove015()
    {
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: 50, specificityCopied: 0), Perf(76.0), null, Date);

        report.Chosen.WarnAbove.ShouldBe(0.05, "failure table row 1: keep the 0.05 default");
        report.Chosen.BlockAbove.ShouldBe(0.15, "failure table row 1: never lower BlockAbove when no n separates");
    }

    /// <summary>
    /// Rule 4 does not depend on rule 1: with no qualifying n the fact threshold is still the midpoint of the fact
    /// classes (0.5 here, fact_copied 1.0 vs fact_paraphrased 0.0).
    /// </summary>
    [Fact]
    public void Calibration_Boundary_NoCandidateN_FactThresholdStillDerivedFromCells()
    {
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: 50, specificityCopied: 0), Perf(76.0), null, Date);

        report.Chosen.CopiedFactThreshold.ShouldBe(0.5);
    }

    [Fact]
    public void Calibration_Boundary_BlockBelowWarnPlusFive_FallsBackTo015WithWarning()
    {
        // A: specificity 90/100 copied gives 77/87 = 0.885057 at n=14, so WarnAbove = 0.89.
        // B: verbatim 93/100 gives 80/87 = 0.919540. The midpoint 0.902299 floors to 0.90 < 0.94, so BlockAbove falls back to 0.15.
        // Known consequence of the literal rule (flagged to the owner): 0.15 sits below WarnAbove 0.89.
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: 93, specificityCopied: 90), Perf(76.0), null, Date);

        report.Chosen.NVi.ShouldBe(14);
        report.Chosen.NEn.ShouldBe(14);
        report.Chosen.WarnAbove.ShouldBe(0.89);
        report.Chosen.BlockAbove.ShouldBe(0.15);
        report.Warnings.Any(warning => warning.Contains("BlockAbove would be 0.9", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public void Calibration_Boundary_BlockExactlyWarnPlusFive_IsAccepted()
    {
        // A: specificity 78/100 copied gives 65/87 = 0.747126, so WarnAbove = 0.75.
        // B: verbatim 92/100 gives 79/87 = 0.908046. The midpoint 0.827586 floors to 0.80 == 0.75 + 0.05, accepted (>=, not >).
        var report = SimilarityCalibrator.Build(SampleSet(verbatimCopied: 92, specificityCopied: 78), Perf(76.0), null, Date);

        report.Chosen.WarnAbove.ShouldBe(0.75);
        report.Chosen.BlockAbove.ShouldBe(0.80);
        report.Warnings.Any(warning => warning.Contains("kept 0.15", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void Calibration_Boundary_FactClassesOverlap_FallsBackTo015AndWarns()
    {
        // fact_copied scores 0 and fact_paraphrased scores 1: max(paraphrased) >= min(copied) -> overlap.
        var report = SimilarityCalibrator.Build(
            SampleSet(verbatimCopied: Length, specificityCopied: 0, copiedFactIsCopied: false, paraphrasedFactIsCopied: true),
            Perf(76.0), null, Date);

        report.Chosen.CopiedFactThreshold.ShouldBe(0.15, "the live Similarity:FactOverlapBlock default");
        report.Warnings.Any(warning => warning.Contains("overlap", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("RoundDown", 0.9749, 0.01, 0.97)]
    [InlineData("RoundDown", 0.97, 0.01, 0.97)]
    [InlineData("RoundDown", 0.9699, 0.01, 0.96)]
    [InlineData("RoundDown", 0.9999, 0.01, 0.99)]
    [InlineData("RoundDown", 0.827586, 0.05, 0.80)]
    [InlineData("RoundUp", 0.0113, 0.01, 0.02)]
    [InlineData("RoundUp", 0.0, 0.01, 0.0)]
    [InlineData("RoundUp", 0.97, 0.01, 0.97)]
    [InlineData("RoundUp", 0.0355, 0.05, 0.05)]
    [InlineData("RoundUp", 0.045, 0.05, 0.05)]
    [InlineData("RoundUp", 0.05, 0.05, 0.05)]
    public void Calibration_Boundary_RoundingSteps_MatchTheRule(string method, double value, double step, double expected)
    {
        var rounding = Helper(method);

        var actual = (decimal)rounding.Invoke(null, [value, (decimal)step])!;

        actual.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData(0.01, 0.97, 0.45)]
    [InlineData(0.10, 0.90, 0.50)]
    [InlineData(0.40, 0.45, 0.40)]
    [InlineData(0.0, 1.0, 0.50)]
    [InlineData(0.747126, 0.908046, 0.80)]
    public void Calibration_Boundary_MidpointBlockAbove_IsFlooredMidpointOfAAndB(double specificityMax, double verbatimMin, double expected)
    {
        SimilarityCalibrator.MidpointBlockAbove(specificityMax, verbatimMin).ShouldBe((decimal)expected);
    }

    [Fact]
    public void Calibration_Boundary_FactOverlapAtZero_FallsBackTo015AndWarnsNotBelowMinCopied()
    {
        // Every fact statement scores 0: max(paraphrased) = min(copied) = 0 is an overlap, so the fallback 0.15 is >= minCopied 0 and CODE-4 warns.
        var report = SimilarityCalibrator.Build(
            SampleSet(verbatimCopied: Length, specificityCopied: 0, copiedFactIsCopied: false, paraphrasedFactIsCopied: false),
            Perf(76.0), null, Date);

        report.Chosen.CopiedFactThreshold.ShouldBe(0.15);
        report.Warnings.Any(warning => warning.Contains("is not below min fact_copied", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public void Calibration_Boundary_ResolvePrevious_WithoutCommittedReport_UsesBaseline()
    {
        var chosen = new CalibrationChoice(14, 14, 0.02, 0.45, 0.05, 163);

        SimilarityCalibrator.ResolvePrevious(null, chosen).ShouldBe(SimilarityCalibrator.Baseline);
    }

    [Fact]
    public void Calibration_Boundary_ResolvePrevious_UnchangedChoice_KeepsCommittedPrevious()
    {
        var chosen = new CalibrationChoice(14, 14, 0.02, 0.45, 0.05, 163);
        var previous = new CalibrationChoice(10, 8, 0.05, 0.15, 0.15, 500);
        var built = SimilarityCalibrator.Build(SampleSet(verbatimCopied: Length, specificityCopied: 0), Perf(76.0), null, Date);
        var committed = built with { Chosen = chosen, Previous = previous };

        SimilarityCalibrator.ResolvePrevious(committed, chosen).ShouldBe(previous);
    }

    [Fact]
    public void Calibration_Boundary_ResolvePrevious_ChangedChoice_UsesCommittedChosen()
    {
        var oldChoice = new CalibrationChoice(14, 14, 0.02, 0.97, 0.05, 152);
        var chosen = new CalibrationChoice(14, 14, 0.02, 0.45, 0.05, 163);
        var built = SimilarityCalibrator.Build(SampleSet(verbatimCopied: Length, specificityCopied: 0), Perf(76.0), null, Date);
        var committed = built with { Chosen = oldChoice, Previous = SimilarityCalibrator.Baseline };

        SimilarityCalibrator.ResolvePrevious(committed, chosen).ShouldBe(oldChoice);
    }

    [Fact]
    public void Calibration_Boundary_ChooseN_CandidateFloorIsInclusiveAt0Point9()
    {
        var warnings = new List<string>();

        var chosen = ChooseN(Cells(n => n == 14 ? 0.9 : 0.5, _ => 0), warnings);

        chosen.ShouldBe(14);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Calibration_Boundary_ChooseN_BelowFloorIsNotACandidate()
    {
        var warnings = new List<string>();

        var chosen = ChooseN(Cells(n => n == 14 ? 0.8999 : 0.5, _ => 0), warnings);

        chosen.ShouldBe(10, "no candidate: the vi default");
        warnings.Count.ShouldBe(1);
    }

    [Fact]
    public void Calibration_Boundary_ChooseN_TiesOnSpecificityChooseTheLargestN()
    {
        var specificity = new Dictionary<int, double> { [6] = 0.2, [8] = 0.05, [10] = 0.01, [12] = 0.01, [14] = 0.02 };
        var warnings = new List<string>();

        var chosen = ChooseN(Cells(_ => 1.0, n => specificity[n]), warnings);

        chosen.ShouldBe(12, "min specificity 0.01 is tied at n=10 and n=12; the largest wins");
    }

    [Fact]
    public void Calibration_Boundary_ChooseN_MinimisesSpecificityOnlyAmongCandidates()
    {
        // n=14 has the lowest specificity but fails the copy floor, so it must not be chosen.
        var verbatim = new Dictionary<int, double> { [6] = 0.95, [8] = 0.95, [10] = 0.95, [12] = 0.95, [14] = 0.5 };
        var specificity = new Dictionary<int, double> { [6] = 0.3, [8] = 0.1, [10] = 0.2, [12] = 0.2, [14] = 0.0 };
        var warnings = new List<string>();

        var chosen = ChooseN(Cells(n => verbatim[n], n => specificity[n]), warnings);

        chosen.ShouldBe(8);
        warnings.ShouldBeEmpty();
    }

    /// <summary>A sample set in which every class has one sample per language, as <c>BuildCells</c> requires.</summary>
    /// <param name="verbatimCopied">Tokens of the 100-token story copied for <c>verbatim_full</c>.</param>
    /// <param name="specificityCopied">Tokens copied for the four specificity classes (and nothing for the rest).</param>
    /// <param name="copiedFactIsCopied">Whether the <c>fact_copied</c> statement is taken verbatim from its source.</param>
    /// <param name="paraphrasedFactIsCopied">Whether the <c>fact_paraphrased</c> statement is taken verbatim from its source.</param>
    private static SimilaritySampleSet SampleSet(
        int verbatimCopied, int specificityCopied, bool copiedFactIsCopied = true, bool paraphrasedFactIsCopied = false)
    {
        var samples = new List<SimilaritySample>();
        foreach (var language in new[] { "vi", "en" })
        {
            foreach (var className in StoryClasses)
            {
                var copied = className == "verbatim_full" ? verbatimCopied
                    : SpecificityClasses.Contains(className) ? specificityCopied
                    : 0;
                samples.Add(StorySample(language, className, copied));
            }

            samples.Add(FactSample(language, "fact_copied", copiedFactIsCopied));
            samples.Add(FactSample(language, "fact_paraphrased", paraphrasedFactIsCopied));
        }

        return new SimilaritySampleSet(SimilarityCalibrator.Schema, samples);
    }

    private static SimilaritySample StorySample(string language, string className, int copied)
    {
        var paragraph = string.Join(' ', Seq("kq", 0, copied).Concat(Seq("uq", copied, Length)));
        return new SimilaritySample(
            $"{className}-{language}",
            language,
            className,
            new SimilaritySource($"src-{className}-{language}", SourceText),
            new CalibrationStory("t", "h", [paragraph], "e"),
            null);
    }

    private static SimilaritySample FactSample(string language, string className, bool copied) => new(
        $"{className}-{language}",
        language,
        className,
        new SimilaritySource($"src-{className}-{language}", SourceText),
        null,
        string.Join(' ', Seq(copied ? "kq" : "uq", 0, Length)));

    private static IEnumerable<string> Seq(string prefix, int from, int to) =>
        Enumerable.Range(from, to - from).Select(i => prefix + Letters(i));

    /// <summary>Encodes a non-negative integer as letters only, so the normaliser keeps every token.</summary>
    private static string Letters(int value) =>
        new(value.ToString(CultureInfo.InvariantCulture).Select(digit => (char)('a' + (digit - '0'))).ToArray());

    private static CalibrationPerf Perf(double p95) => new(p95, "synthetic");

    /// <summary>Cells for the <c>vi</c> language where verbatim_full min and every specificity max come from the functions.</summary>
    private static List<CalibrationCell> Cells(Func<int, double> verbatimMin, Func<int, double> specificityMax)
    {
        var cells = new List<CalibrationCell>();
        foreach (var n in Grid)
        {
            cells.Add(new CalibrationCell("verbatim_full", "vi", n, verbatimMin(n), verbatimMin(n), verbatimMin(n), verbatimMin(n)));
            foreach (var className in SpecificityClasses)
            {
                var max = specificityMax(n);
                cells.Add(new CalibrationCell(className, "vi", n, max, max, 0, max));
            }
        }

        return cells;
    }

    private static int ChooseN(IReadOnlyList<CalibrationCell> cells, List<string> warnings)
    {
        var method = typeof(SimilarityCalibrator).GetMethod("ChooseN", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ChooseN not found.");
        return (int)method.Invoke(null, [cells, "vi", warnings])!;
    }

    private static MethodInfo Helper(string name) =>
        typeof(SimilarityCalibrator).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{name} not found.");
}
