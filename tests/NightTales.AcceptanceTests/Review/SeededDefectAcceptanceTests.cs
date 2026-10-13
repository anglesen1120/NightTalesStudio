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
/// keychain access, so the suite is safe to run on a machine with no provider key. A missing key or a provider error
/// aborts before any report is written, with the failure table naming the case.
/// </para>
/// <para>
/// The cost cap (default USD 5 per pass, <c>NTS_ACCEPTANCE_MAX_USD</c> to override) is checked before every paid call
/// and before the second model; when it is reached the pass is marked <c>partial</c> and no threshold decides
/// anything. The planned call count is exactly <see cref="SeededDefectRun.CallsPerModel"/> per model — no retries
/// beyond the client's own — and it is printed before the first call.
/// </para>
/// <para>
/// <b>Cross-check that needs a DB-backed run.</b> <see cref="SeededDefectRun"/> meters in memory (real
/// <c>TextCostCalculator</c>, shipped rates, no database write), so the acceptance row "one <c>usage_records</c> row
/// per provider call with the measured cost" is not decided here. The orchestrator runs that row against a real
/// database (Worker's <c>UsageMeter</c> + <c>ConnectionStrings__Postgres</c>) and compares the summed cost with the
/// report's <c>cost_usd</c>.
/// </para>
/// </remarks>
/// <param name="run">The per-process pass cache: each model is called at most once.</param>
[Trait("Category", "Acceptance")]
public sealed class SeededDefectAcceptanceTests(SeededDefectRun run) : IClassFixture<SeededDefectRun>
{
    /// <summary>The D15 recall gate.</summary>
    public const double RecallThreshold = 0.80;

    /// <summary>The D15 false-block gate.</summary>
    public const double FalseBlockThreshold = 0.10;

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
            pass.ReportPath.ShouldNotBeNull($"{pass.Model}: the pass wrote no report.");
            File.Exists(pass.ReportPath).ShouldBeTrue($"{pass.Model}: the report file is missing.");
            pass.ReportPath.ShouldContain(Path.Combine("trial", "review"));

            var json = File.ReadAllText(pass.ReportPath!);

            // D19: the report carries ids, wire names, counts, numbers, the model and timestamps — never story text.
            var sample = SeededDraft.Load(SeededDraft.Ids()[0]);
            json.ShouldNotContain(sample.Brief);
            json.ShouldNotContain(sample.Draft.Title);
            json.ShouldNotContain(sample.Draft.Paragraphs[0].Text);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            root.GetProperty("model").GetString().ShouldBe(pass.Model);
            root.GetProperty("partial").GetBoolean().ShouldBe(pass.Partial);
            root.GetProperty("drafts").GetArrayLength().ShouldBe(pass.Drafts.Count);
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
            Console.WriteLine($"{pass.Model}: pass is partial (USD cap reached); no threshold is asserted.");
            return;
        }

        pass.Report.ShouldNotBeNull($"{pass.Model}: the pass completed but produced no report.");
        var report = pass.Report!;

        report.Recall.ShouldBeGreaterThanOrEqualTo(
            RecallThreshold,
            FormattableString.Invariant(
                $"{pass.Model}: recall {report.Recall:0.0000} is below the D15 threshold {RecallThreshold:0.00} ({report.DetectedDefects}/{report.SeededDefects} planted defects detected)."));

        report.FalseBlockRate.ShouldBeLessThanOrEqualTo(
            FalseBlockThreshold,
            FormattableString.Invariant(
                $"{pass.Model}: false-block rate {report.FalseBlockRate:0.0000} is above the D15 threshold {FalseBlockThreshold:0.00} ({report.CleanDraftsWithBlocking}/{report.CleanDrafts} clean drafts blocked)."));
    }

    private static void PrintComparison(params AcceptanceModelRun[] passes)
    {
        Console.WriteLine();
        Console.WriteLine("| model | recall | false-block | cost usd | drafts | partial |");
        Console.WriteLine("|---|---|---|---|---|---|");
        foreach (var pass in passes)
        {
            Console.WriteLine(
                FormattableString.Invariant(
                    $"| {pass.Model} | {Number(pass.Report?.Recall)} | {Number(pass.Report?.FalseBlockRate)} | {pass.Report?.CostUsd ?? 0m:0.0000} | {pass.Drafts.Count} | {pass.Partial} |"));
        }

        foreach (var pass in passes.Where(pass => pass.Report is not null))
        {
            foreach (var pair in pass.Report!.RecallByType.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(FormattableString.Invariant($"| {pass.Model} | {pair.Key} | {pair.Value:0.0000} |"));
            }
        }

        Console.WriteLine();
    }

    private static string Number(double? value) =>
        value is { } measured ? measured.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";
}
