using System.Globalization;
using System.Text.Json;
using NightTales.TestKit.Review;
using Shouldly;
using Xunit;

namespace NightTales.AcceptanceTests.Review;

/// <summary>
/// The live end of 02.6.1-T2 (02 README D15/D20/T25): every fixture of the frozen seeded-defect set is reviewed by
/// the real prompt catalog, adapter and writer, once per candidate model, and the measured recall / false-block rate
/// decide which model <c>OpenAi:Models:Review</c> is pinned to.
/// </summary>
/// <remarks>
/// <para>
/// Gated by <c>OPENAI_LIVE_TESTS=1</c> (D20): without it each test is skipped before any client, configuration or
/// keychain access, so the suite is safe to run on a machine with no provider key.
/// </para>
/// <para>
/// The cost cap (default USD 5, <c>NTS_ACCEPTANCE_MAX_USD</c> to override) is <b>one cumulative cap for both
/// models</b>: it is checked before every paid call and before the second model, against the shared meter plus the
/// next call's worst-case bound. A pass the cap stops is <c>partial</c> and the test fails — a partial pass records
/// no decision, so it must not look green. A provider error aborts before any report with the spent USD and call
/// count in the message.
/// </para>
/// <para>
/// <b>Cross-check that needs a DB-backed run.</b> <see cref="SeededDefectRun"/> meters in memory (real
/// <c>TextCostCalculator</c>, shipped rates, no database write), so the acceptance row "one <c>usage_records</c> row
/// per provider call with the measured cost" is not decided here. The orchestrator runs that row against a real
/// database (Worker's <c>UsageMeter</c> + <c>ConnectionStrings__Postgres</c>) and compares the summed cost with the
/// report's <c>pass_cost_usd</c>.
/// </para>
/// </remarks>
/// <param name="run">The per-process pass cache: each model is called at most once.</param>
[Trait("Category", "Acceptance")]
public sealed class SeededDefectAcceptanceTests(SeededDefectRun run) : IClassFixture<SeededDefectRun>
{
    [Fact]
    public async Task SeededDefectSet_Luna_MeetsRecallAndFalseBlockThresholds()
    {
        Assert.SkipWhen(!LiveGate.OpenAiLive, LiveGate.Reason);

        var pass = await run.RunAsync(SeededDefectRun.LunaModel, TestContext.Current.CancellationToken);

        AssertThresholds(pass);
    }

    [Fact]
    public async Task SeededDefectSet_Sol_MeetsRecallAndFalseBlockThresholds()
    {
        Assert.SkipWhen(!LiveGate.OpenAiLive, LiveGate.Reason);

        var pass = await run.RunAsync(SeededDefectRun.SolModel, TestContext.Current.CancellationToken);

        AssertThresholds(pass);
    }

    [Fact]
    public async Task SeededDefectSet_ReportWrittenToTrialDirectory()
    {
        Assert.SkipWhen(!LiveGate.OpenAiLive, LiveGate.Reason);

        // The pass cache keeps this at most one real call per model per process.
        var luna = await run.RunAsync(SeededDefectRun.LunaModel, TestContext.Current.CancellationToken);
        var sol = await run.RunAsync(SeededDefectRun.SolModel, TestContext.Current.CancellationToken);

        foreach (var pass in new[] { luna, sol })
        {
            File.Exists(pass.ReportPath).ShouldBeTrue($"{pass.Model}: the report file is missing at '{pass.ReportPath}'.");
            pass.ReportPath.ShouldContain(Path.Combine("trial", "review"));

            var json = File.ReadAllText(pass.ReportPath);

            // D19: no fixture's story text — title, brief, every paragraph and every defect note — may appear in the
            // report or in any console line the pass printed. Checked over the whole set, not a sample.
            foreach (var draft in SeededDraft.LoadAll())
            {
                foreach (var storyText in StoryTexts(draft))
                {
                    json.ShouldNotContain(storyText, customMessage: $"{pass.Model}: the report leaked story text from fixture '{draft.Id}'.");
                    foreach (var line in pass.ConsoleLines)
                    {
                        line.ShouldNotContain(storyText, customMessage: $"{pass.Model}: the console leaked story text from fixture '{draft.Id}'.");
                    }
                }
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            root.GetProperty("model").GetString().ShouldBe(pass.Model);
            root.GetProperty("partial").GetBoolean().ShouldBe(pass.Partial);
            root.GetProperty("drafts").GetArrayLength().ShouldBe(pass.Drafts.Count);
            root.GetProperty("pass_cost_usd").GetDecimal().ShouldBe(pass.PassCostUsd);
            root.GetProperty("recall_threshold").GetDouble().ShouldBe(SeededModelDecision.RecallThreshold);
            root.GetProperty("false_block_threshold").GetDouble().ShouldBe(SeededModelDecision.FalseBlockThreshold);
            root.GetProperty("eligible").GetBoolean().ShouldBe(SeededModelDecision.IsEligible(Pass(pass)));

            // The name's stamp and the file's generated_at are one timestamp.
            Path.GetFileNameWithoutExtension(pass.ReportPath)
                .ShouldStartWith($"acceptance-{pass.Model}-{Stamp(pass)}");
            StampOf(root).ShouldBe(pass.GeneratedAt);

            var statusRoot = root.GetProperty("status_counts");
            statusRoot.EnumerateObject().Count().ShouldBe(pass.StatusCounts.Count);
            foreach (var pair in pass.StatusCounts)
            {
                statusRoot.GetProperty(pair.Key).GetInt32().ShouldBe(pair.Value);
            }

            if (pass.Report is { } measured)
            {
                var report = root.GetProperty("report");
                report.GetProperty("recall").GetDouble().ShouldBe(measured.Recall, 0.000001);
                report.GetProperty("false_block_rate").GetDouble().ShouldBe(measured.FalseBlockRate, 0.000001);
                report.GetProperty("seeded_defects").GetInt32().ShouldBe(measured.SeededDefects);
                report.GetProperty("cost_usd").GetDecimal().ShouldBe(measured.CostUsd);
            }
        }

        PrintComparison(luna, sol);
    }

    private static void AssertThresholds(AcceptanceModelRun pass)
    {
        if (pass.Partial)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"{pass.Model}: pass is partial (spent {pass.SpentUsd:0.0000} of the {pass.CapUsd:0.00} USD cap over {pass.Drafts.Count} reviewed drafts); no decision is recorded."));
            false.ShouldBeTrue(FormattableString.Invariant(
                $"{pass.Model}: the pass is partial — the USD cap stopped it at {pass.SpentUsd:0.0000} of {pass.CapUsd:0.00} after {pass.Drafts.Count} of {SeededDefectRun.CallsPerModel} drafts — and no decision is recorded; a partial pass must fail, not pass green."));
            return;
        }

        pass.Report.ShouldNotBeNull($"{pass.Model}: the pass completed but produced no report.");
        var report = pass.Report!;

        report.Recall.ShouldBeGreaterThanOrEqualTo(
            SeededModelDecision.RecallThreshold,
            FormattableString.Invariant(
                $"{pass.Model}: recall {report.Recall:0.0000} is below the D15 threshold {SeededModelDecision.RecallThreshold:0.00} ({report.DetectedDefects}/{report.SeededDefects} planted defects detected)."));

        report.FalseBlockRate.ShouldBeLessThanOrEqualTo(
            SeededModelDecision.FalseBlockThreshold,
            FormattableString.Invariant(
                $"{pass.Model}: false-block rate {report.FalseBlockRate:0.0000} is above the D15 threshold {SeededModelDecision.FalseBlockThreshold:0.00} ({report.CleanDraftsWithBlocking}/{report.CleanDrafts} clean drafts blocked)."));

        SeededModelDecision.IsEligible(Pass(pass)).ShouldBeTrue($"{pass.Model}: the pass is not eligible for the D15 decision.");
    }

    private static void PrintComparison(params AcceptanceModelRun[] passes)
    {
        var luna = passes.FirstOrDefault(pass => pass.Model == SeededDefectRun.LunaModel);
        var sol = passes.FirstOrDefault(pass => pass.Model == SeededDefectRun.SolModel);
        var decision = luna is not null && sol is not null ? SeededModelDecision.Decide(Pass(luna), Pass(sol)) : null;

        Console.WriteLine();
        Console.WriteLine("| model | recall | false-block | pass cost usd | drafts | partial | eligible | pinned |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var pass in passes)
        {
            var pinned = decision is not null && Pinned(decision.Choice) == pass.Model;
            Console.WriteLine(
                FormattableString.Invariant(
                    $"| {pass.Model} | {Number(pass.Report?.Recall)} | {Number(pass.Report?.FalseBlockRate)} | {pass.PassCostUsd:0.0000} | {pass.Drafts.Count} | {pass.Partial} | {SeededModelDecision.IsEligible(Pass(pass))} | {pinned} |"));
        }

        Console.WriteLine();
        Console.WriteLine("| model | issue type | recall |");
        Console.WriteLine("|---|---|---|");
        foreach (var pass in passes.Where(pass => pass.Report is not null))
        {
            foreach (var pair in pass.Report!.RecallByType.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(FormattableString.Invariant($"| {pass.Model} | {pair.Key} | {pair.Value:0.0000} |"));
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            decision is null
                ? "D15 decision: n/a (a pass is missing)."
                : FormattableString.Invariant($"D15 decision: {decision.Choice.ToString().ToLowerInvariant()}; pinned model: {Pinned(decision.Choice) ?? "none"}."));
        Console.WriteLine();
    }

    /// <summary>Every story-text sample D19 must keep out of the report and the console (empty samples are skipped).</summary>
    private static IEnumerable<string> StoryTexts(SeededDraft draft)
    {
        var samples = new List<string> { draft.Draft.Title, draft.Brief };
        samples.AddRange(draft.Draft.Paragraphs.Select(paragraph => paragraph.Text));
        samples.AddRange(draft.SeedDefects.Select(defect => defect.Note));

        return samples.Where(sample => !string.IsNullOrWhiteSpace(sample));
    }

    private static SeededModelPass Pass(AcceptanceModelRun run) => new(run.Report, run.Partial);

    private static string? Pinned(SeededModelChoice choice) => choice switch
    {
        SeededModelChoice.Luna => SeededDefectRun.LunaModel,
        SeededModelChoice.Sol => SeededDefectRun.SolModel,
        _ => null,
    };

    private static string Stamp(AcceptanceModelRun run) =>
        run.GeneratedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static DateTimeOffset StampOf(JsonElement root) =>
        DateTimeOffset.Parse(root.GetProperty("generated_at").GetString()!, CultureInfo.InvariantCulture);

    private static string Number(double? value) =>
        value is { } measured ? measured.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";
}
