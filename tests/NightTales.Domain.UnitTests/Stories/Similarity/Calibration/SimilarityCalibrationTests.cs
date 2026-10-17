using System.Globalization;
using System.Text;
using System.Text.Json;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity.Calibration;

/// <summary>
/// The offline acceptance tests of 02.5.6-T2: they read the committed calibration report and re-derive
/// every decision from the committed fixture, so a fixture, normaliser, shingler or scorer change without
/// a re-run fails the build.
/// </summary>
public sealed class SimilarityCalibrationTests
{
    private static readonly string[] ConfigPaths =
    [
        Path.Combine("src", "NightTales.Api", "appsettings.json"),
        Path.Combine("src", "NightTales.Worker", "appsettings.json"),
    ];

    /// <summary>
    /// Every configuration key that must carry the chosen copied-fact threshold. Both names answered the same
    /// question before the calibration was applied; the orchestrator may reduce this list to the live key.
    /// </summary>
    private static readonly string[] FactThresholdKeys =
    [
        "Research:FactSheet:CopiedFactThreshold",
        "Similarity:FactOverlapBlock",
    ];

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_Fixture_EveryClassHasAtLeastThreeSamplesPerLanguage()
    {
        var fixture = SimilarityCalibrator.LoadFixture();

        fixture.Samples.Count.ShouldBeGreaterThanOrEqualTo(60);
        fixture.Samples.Select(static sample => sample.Id).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(fixture.Samples.Count);
        foreach (var language in SimilarityCalibrator.LanguageNames)
        {
            foreach (var className in SimilarityCalibrator.ClassNames)
            {
                fixture.Samples.Count(sample => sample.Language == language && sample.Class == className)
                    .ShouldBeGreaterThanOrEqualTo(3, $"{language}/{className}");
            }
        }

        foreach (var sample in fixture.Samples.Where(static sample => sample.Story is not null))
        {
            var options = SimilarityCalibrator.Options(sample.Language, SimilarityCalibrator.Grid[0]);
            SimilarityScorer.IsScorable(
                    SimilarityCalibrator.StoryTextOf(sample), [SimilarityCalibrator.CorpusOf(sample)], options)
                .ShouldBeTrue($"story sample '{sample.Id}' must be scorable");
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_Sweep_CoversEveryClassLanguageAndCandidateN()
    {
        var fixture = SimilarityCalibrator.LoadFixture();
        var cells = SimilarityCalibrator.Sweep(fixture);
        var report = ReadReport();

        var expectedCount = SimilarityCalibrator.ClassNames.Count * SimilarityCalibrator.LanguageNames.Count
            * SimilarityCalibrator.Grid.Count;
        cells.Count.ShouldBe(expectedCount);
        report.Cells.Count.ShouldBe(expectedCount);
        cells.Select(static cell => (cell.Class, cell.Language, cell.N)).Distinct().Count().ShouldBe(expectedCount);
        report.Cells.Select(static cell => (cell.Class, cell.Language, cell.N)).Distinct().Count().ShouldBe(expectedCount);
        foreach (var language in SimilarityCalibrator.LanguageNames)
        {
            foreach (var className in SimilarityCalibrator.ClassNames)
            {
                foreach (var n in SimilarityCalibrator.Grid)
                {
                    var cell = Cell(cells, className, language, n);
                    cell.Min.ShouldBeLessThanOrEqualTo(cell.P50, $"{className}/{language}/n={n}");
                    cell.P50.ShouldBeLessThanOrEqualTo(cell.Max, $"{className}/{language}/n={n}");
                    cell.Min.ShouldBeGreaterThanOrEqualTo(0);
                    cell.Max.ShouldBeLessThanOrEqualTo(1);
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_FalseBlockRate_IsZeroOnOriginalAndParaphrase()
    {
        var report = ReadReport();
        foreach (var language in SimilarityCalibrator.LanguageNames)
        {
            var n = ChosenN(report, language);
            foreach (var className in SimilarityCalibrator.SpecificityClassNames)
            {
                Cell(report.Cells, className, language, n).Max
                    .ShouldBeLessThanOrEqualTo(report.Chosen.WarnAbove, $"{className}/{language}/n={n}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_CopyDetectionRate_AtLeast90Percent()
    {
        var report = ReadReport();
        var samples = SimilarityCalibrator.ScoreSamples(SimilarityCalibrator.LoadFixture());
        var copies = samples
            .Where(score => score.Class == "verbatim_full" && score.N == ChosenN(report, score.Language))
            .ToArray();
        copies.Length.ShouldBeGreaterThan(0);

        var above = copies.Count(score => score.Score > report.Chosen.BlockAbove);
        ((double)above / copies.Length).ShouldBeGreaterThanOrEqualTo(
            0.9, $"{above}/{copies.Length} verbatim_full samples score above BlockAbove");
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_ChosenN_MaximisesSpecificityUnderTheCopyFloor()
    {
        var report = ReadReport();
        foreach (var language in SimilarityCalibrator.LanguageNames)
        {
            var n = ChosenN(report, language);
            n.ShouldBe(ExpectedN(report, language));
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_BlockAbove_IsAtLeastWarnAbovePlusFivePoints()
    {
        var report = ReadReport();
        var warn = ToDecimal(report.Chosen.WarnAbove);
        var block = ToDecimal(report.Chosen.BlockAbove);

        block.ShouldBeGreaterThanOrEqualTo(warn + 0.05m);
        block.ShouldBeGreaterThanOrEqualTo(0.01m);
        block.ShouldBeLessThanOrEqualTo(1m);
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_FactThreshold_SeparatesCopiedFromParaphrasedFacts()
    {
        var report = ReadReport();
        var samples = SimilarityCalibrator.ScoreSamples(SimilarityCalibrator.LoadFixture());

        var copied = samples
            .Where(score => score.Class == "fact_copied" && score.N == ChosenN(report, score.Language))
            .ToArray();
        var paraphrased = samples
            .Where(score => score.Class == "fact_paraphrased" && score.N == ChosenN(report, score.Language))
            .ToArray();
        copied.Length.ShouldBeGreaterThan(0);
        paraphrased.Length.ShouldBeGreaterThan(0);

        foreach (var score in copied)
        {
            score.Score.ShouldBeGreaterThan(report.Chosen.CopiedFactThreshold, $"fact_copied '{score.SampleId}'");
        }

        foreach (var score in paraphrased)
        {
            score.Score.ShouldBeLessThan(report.Chosen.CopiedFactThreshold, $"fact_paraphrased '{score.SampleId}'");
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_TranslatedCopy_IsDocumentedAsResidual()
    {
        var report = ReadReport();
        var samples = SimilarityCalibrator.ScoreSamples(SimilarityCalibrator.LoadFixture());
        var enToVi = Mean(samples, "translated_copy", "vi", report.Chosen.NVi);
        var viToEn = Mean(samples, "translated_copy", "en", report.Chosen.NEn);

        report.TranslatedCopyResidual.ShouldContain(
            FormattableString.Invariant($"en source -> vi story mean {enToVi:F6}"));
        report.TranslatedCopyResidual.ShouldContain(
            FormattableString.Invariant($"vi source -> en story mean {viToEn:F6}"));
        report.TranslatedCopyResidual.ShouldContain(
            "Cross-language translation copying is not detected (F18); mitigated by facts-only writer input (02.5.5).");
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_DilutedVerbatimParagraph_IsWarned()
    {
        var report = ReadReport();
        var samples = SimilarityCalibrator.ScoreSamples(SimilarityCalibrator.LoadFixture());
        var paragraphs = samples
            .Where(score => score.Class == "verbatim_paragraph" && score.N == ChosenN(report, score.Language))
            .ToArray();
        paragraphs.Length.ShouldBeGreaterThan(0);
        report.Dilution.Count.ShouldBe(paragraphs.Length);

        foreach (var score in paragraphs)
        {
            var diagnostic = report.Dilution.SingleOrDefault(
                entry => entry.Id == score.SampleId && entry.Language == score.Language);
            diagnostic.ShouldNotBeNull($"verbatim_paragraph '{score.SampleId}' must be reported as a dilution");
            diagnostic!.Score.ShouldBe(score.Score);
            if (score.Score < report.Chosen.BlockAbove)
            {
                report.Warnings.Any(warning => warning.Contains(score.SampleId, StringComparison.Ordinal))
                    .ShouldBeTrue($"a diluted sample '{score.SampleId}' must carry a warning");
            }
        }

        // BlockAbove follows rule 3 (owner, 2026-10-17): the midpoint of A (max specificity) and B (min verbatim_full),
        // floored to 0.05, with the WarnAbove + 0.05 fallback. verbatim_paragraph is never an input.
        var specificityMax = SimilarityCalibrator.BlockMaxClassNames
            .SelectMany(className => SimilarityCalibrator.LanguageNames.Select(language =>
                Cell(report.Cells, className, language, ChosenN(report, language)).Max))
            .Max();
        var verbatimMin = SimilarityCalibrator.LanguageNames
            .Min(language => Cell(report.Cells, "verbatim_full", language, ChosenN(report, language)).Min);
        var block = SimilarityCalibrator.MidpointBlockAbove(specificityMax, verbatimMin);
        if (block < ToDecimal(report.Chosen.WarnAbove) + 0.05m)
        {
            block = 0.15m;
        }

        block.ShouldBe(ToDecimal(report.Chosen.BlockAbove));
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_Report_IsDeterministic()
    {
        var report = ReadReport();
        var fixture = SimilarityCalibrator.LoadFixture();
        var first = SimilarityCalibrator.SerializeReport(
            SimilarityCalibrator.Build(fixture, report.Perf, report.Previous, "2000-01-01"));
        var second = SimilarityCalibrator.SerializeReport(
            SimilarityCalibrator.Build(fixture, report.Perf, report.Previous, "2000-01-02"));

        first.ShouldNotBe(second);
        first.Replace("2000-01-01", "<generatedAt>", StringComparison.Ordinal)
            .ShouldBe(second.Replace("2000-01-02", "<generatedAt>", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_ConfigMatchesReport()
    {
        var report = ReadReport();
        foreach (var relativePath in ConfigPaths)
        {
            var path = Path.Combine(SimilarityCalibrator.RepositoryRoot(), relativePath);
            File.Exists(path).ShouldBeTrue(path);
            ReadInt(path, "Similarity", "N", "vi").ShouldBe(report.Chosen.NVi, path);
            ReadInt(path, "Similarity", "N", "en").ShouldBe(report.Chosen.NEn, path);
            ReadDouble(path, "Similarity", "WarnAbove").ShouldBe(report.Chosen.WarnAbove, 0.0000005, path);
            ReadDouble(path, "Similarity", "BlockAbove").ShouldBe(report.Chosen.BlockAbove, 0.0000005, path);
            ReadInt(path, "Similarity", "Perf", "BudgetMs").ShouldBe(report.Chosen.PerfBudgetMs, path);
            foreach (var key in FactThresholdKeys)
            {
                ReadDouble(path, key.Split(':', StringSplitOptions.RemoveEmptyEntries))
                    .ShouldBe(report.Chosen.CopiedFactThreshold, 0.0000005, $"{path} / {key}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_PerfBudget_IsTwiceMeasuredP95()
    {
        var report = ReadReport();
        report.Chosen.PerfBudgetMs.ShouldBe((int)Math.Ceiling(2 * report.Perf.MeasuredP95Ms));
        foreach (var relativePath in ConfigPaths)
        {
            var path = Path.Combine(SimilarityCalibrator.RepositoryRoot(), relativePath);
            ReadInt(path, "Similarity", "Perf", "BudgetMs").ShouldBe(report.Chosen.PerfBudgetMs, path);
        }
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_Report_MatchesFixtureAndCode()
    {
        var committed = File.ReadAllText(SimilarityCalibrator.ReportPath);
        var report = SimilarityCalibrator.ParseReport(committed);
        var rebuilt = SimilarityCalibrator.Build(
            SimilarityCalibrator.LoadFixture(), report.Perf, report.Previous, report.GeneratedAt);

        SimilarityCalibrator.SerializeReport(rebuilt).TrimEnd('\n').ShouldBe(committed.TrimEnd('\n'));
    }

    [Fact]
    [Trait("Category", "Calibration")]
    public void Calibration_Report_Regenerate()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NTS_CALIBRATION_WRITE"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set NTS_CALIBRATION_WRITE=1 to regenerate the committed calibration report.");
        }

        var p95Text = Environment.GetEnvironmentVariable("NTS_CALIBRATION_PERF_P95_MS");
        if (string.IsNullOrWhiteSpace(p95Text))
        {
            throw new InvalidOperationException(
                "NTS_CALIBRATION_PERF_P95_MS is required when NTS_CALIBRATION_WRITE=1 (the p95_ms of SimilarityPerfTests).");
        }

        var p95 = double.Parse(p95Text, CultureInfo.InvariantCulture);
        var perfSource = Environment.GetEnvironmentVariable("NTS_CALIBRATION_PERF_SOURCE");
        var perf = new CalibrationPerf(
            p95,
            string.IsNullOrWhiteSpace(perfSource)
                ? "tests/NightTales.Performance.Tests SimilarityPerfTests at n=14 (median of 10 runs' p95), Apple M4 macOS"
                : perfSource);
        var fixture = SimilarityCalibrator.LoadFixture();
        var committed = File.Exists(SimilarityCalibrator.ReportPath)
            ? SimilarityCalibrator.ParseReport(File.ReadAllText(SimilarityCalibrator.ReportPath))
            : null;
        var generatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var provisional = SimilarityCalibrator.Build(fixture, perf, null, generatedAt);
        var report = SimilarityCalibrator.Build(
            fixture, perf, SimilarityCalibrator.ResolvePrevious(committed, provisional.Chosen), generatedAt);

        File.WriteAllText(
            SimilarityCalibrator.ReportPath, SimilarityCalibrator.SerializeReport(report), new UTF8Encoding(false));

        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine("class,language,n,mean,p50,min,max");
        foreach (var cell in report.Cells)
        {
            output.WriteLine(FormattableString.Invariant(
                $"{cell.Class},{cell.Language},{cell.N},{cell.Mean:F6},{cell.P50:F6},{cell.Min:F6},{cell.Max:F6}"));
        }

        output.WriteLine(FormattableString.Invariant(
            $"chosen: nVi={report.Chosen.NVi} nEn={report.Chosen.NEn} warnAbove={report.Chosen.WarnAbove:F2} blockAbove={report.Chosen.BlockAbove:F2} copiedFactThreshold={report.Chosen.CopiedFactThreshold:F2} perfBudgetMs={report.Chosen.PerfBudgetMs}"));
        output.WriteLine($"samples={report.SampleCount} warnings={report.Warnings.Count}");
        foreach (var warning in report.Warnings)
        {
            output.WriteLine("warning: " + warning);
        }
    }

    private static CalibrationReport ReadReport()
    {
        var path = SimilarityCalibrator.ReportPath;
        File.Exists(path).ShouldBeTrue(
            $"the committed calibration report is missing: run Calibration_Report_Regenerate ({path})");
        return SimilarityCalibrator.ParseReport(File.ReadAllText(path));
    }

    private static int ChosenN(CalibrationReport report, string language) =>
        language == "vi" ? report.Chosen.NVi : report.Chosen.NEn;

    private static int ExpectedN(CalibrationReport report, string language)
    {
        var candidates = SimilarityCalibrator.Grid
            .Where(n => Cell(report.Cells, "verbatim_full", language, n).Min >= 0.9)
            .ToArray();
        if (candidates.Length == 0)
        {
            return language == "vi" ? 10 : 8;
        }

        var specificity = candidates.ToDictionary(
            static n => n,
            n => SimilarityCalibrator.SpecificityClassNames.Max(
                className => Cell(report.Cells, className, language, n).Max));
        var best = specificity.Values.Min();
        return specificity.Where(pair => pair.Value == best).Select(static pair => pair.Key).Max();
    }

    private static CalibrationCell Cell(
        IReadOnlyList<CalibrationCell> cells, string className, string language, int n) =>
        cells.Single(cell => cell.Class == className && cell.Language == language && cell.N == n);

    private static double Mean(
        IReadOnlyList<SampleScore> scores, string className, string language, int n)
    {
        var values = scores
            .Where(score => score.Class == className && score.Language == language && score.N == n)
            .Select(static score => score.Score)
            .OrderBy(static value => value)
            .ToArray();
        return values.Length == 0 ? 0 : Math.Round(values.Average(), 6, MidpointRounding.AwayFromZero);
    }

    private static decimal ToDecimal(double value) =>
        decimal.Parse(value.ToString("F6", CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static JsonElement ReadElement(string configPath, params string[] path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(configPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var element = document.RootElement;
        foreach (var key in path)
        {
            if (!element.TryGetProperty(key, out var child))
            {
                throw new InvalidOperationException($"{configPath} has no '{string.Join(':', path)}'.");
            }

            element = child;
        }

        return element.Clone();
    }

    private static int ReadInt(string configPath, params string[] path) => ReadElement(configPath, path).GetInt32();

    private static double ReadDouble(string configPath, params string[] path) => ReadElement(configPath, path).GetDouble();
}
