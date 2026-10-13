using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.DependencyInjection;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit.Review;

namespace NightTales.AcceptanceTests.Review;

/// <summary>One type/segment pair of a seeded defect or a reported issue (D19: ids and wire names only).</summary>
/// <param name="Type">The <c>review.v1</c> issue type wire name.</param>
/// <param name="Segment">The D22 segment address.</param>
public sealed record DefectRef(string Type, string Segment);

/// <summary>What one model reported for one draft: the planted defects and the ones it matched (D19: no text).</summary>
/// <param name="Id">The fixture id.</param>
/// <param name="Seeded">The planted defects.</param>
/// <param name="Detected">The planted defects an issue matched.</param>
/// <param name="BlockingOnClean">The blocking issue types on a clean draft, ordinal order; empty for a seeded draft.</param>
public sealed record AcceptanceDraftOutcome(
    string Id,
    IReadOnlyList<DefectRef> Seeded,
    IReadOnlyList<DefectRef> Detected,
    IReadOnlyList<string> BlockingOnClean);

/// <summary>The outcome of one model's pass over the frozen set (02.6.1-T2).</summary>
/// <param name="Model">The model under test.</param>
/// <param name="GeneratedAt">When the pass finished.</param>
/// <param name="Partial"><see langword="true"/> when the USD cap stopped the pass before every draft was reviewed.</param>
/// <param name="ReportPath">The written report, or <see langword="null"/> when nothing was scored.</param>
/// <param name="Drafts">Per-draft outcomes; only the drafts actually reviewed.</param>
/// <param name="Report">The measured report, or <see langword="null"/> when the partial pass was not scorable.</param>
public sealed record AcceptanceModelRun(
    string Model,
    DateTimeOffset GeneratedAt,
    bool Partial,
    string? ReportPath,
    List<AcceptanceDraftOutcome> Drafts,
    SeededDefectReport? Report);

/// <summary>
/// The live engine of the seeded-defect acceptance set (02.6.1-T2 §Design, D15/D19/D20/T25): it composes the real
/// prompt catalog, text adapter and writer exactly as the Worker does, reviews every fixture once per model, meters
/// the calls with the real <see cref="TextCostCalculator"/> at the shipped rates, scores the outcome with
/// <see cref="SeededDefectScorer"/> and writes <c>trial/review/acceptance-&lt;model&gt;-&lt;stamp&gt;.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No work happens in the constructor.</b> A pass that is skipped through <see cref="LiveGate.OpenAiLive"/> must not
/// touch the environment, the keychain, the filesystem or the network, and xunit builds the class fixture before the
/// skip is evaluated. The environment and the key are therefore read on the first <see cref="RunAsync"/>, which no
/// skipped test reaches.
/// </para>
/// <para>
/// <b>Metering.</b> The cheapest faithful meter is used: <see cref="InMemoryUsageMeter"/> prices each call with the
/// real <see cref="TextCostCalculator"/> at the shipped rates (<see cref="PricingOptions.DefaultModels"/>, the in-code
/// default of <c>OpenAi:Pricing</c>) and writes nothing to the database. The <c>usage_records</c> cross-check
/// acceptance row therefore needs a DB-backed run; the orchestrator runs it by pointing
/// <c>ConnectionStrings__Postgres</c> at a real database and swapping <see cref="InMemoryUsageMeter"/> for the
/// Worker's <c>UsageMeter</c> (same rates source, one row per <c>usage_records</c>).
/// </para>
/// <para>
/// <b>D19.</b> The report and every console line carry ids, wire names, counts, numbers, the model and timestamps
/// only — never story text, evidence quotes or notes.
/// </para>
/// </remarks>
public sealed class SeededDefectRun
{
    /// <summary>The environment variable that gates every live test (02 README D20).</summary>
    public const string LiveEnvVar = "OPENAI_LIVE_TESTS";

    /// <summary>The environment variable holding the provider key.</summary>
    public const string ApiKeyEnvVar = "OPENAI_API_KEY";

    /// <summary>The environment variable overriding the USD cap (T25).</summary>
    public const string MaxUsdEnvVar = "NTS_ACCEPTANCE_MAX_USD";

    /// <summary>The default hard stop for one model pass, in USD (T25).</summary>
    public const decimal DefaultMaxUsd = 5m;

    /// <summary>The cheapest candidate model (the shipped <c>OpenAi:Models:Review</c>).</summary>
    public const string LunaModel = "gpt-6-luna";

    /// <summary>The stronger candidate model.</summary>
    public const string SolModel = "gpt-6-sol";

    private const string KeychainService = "nts.openai.story";

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<AcceptanceModelRun>> _runs = new(StringComparer.Ordinal);
    private readonly InMemoryUsageMeter _meter;
    private decimal? _capUsd;

    /// <summary>Creates the engine; it reads nothing until <see cref="RunAsync"/> runs.</summary>
    public SeededDefectRun() => _meter = new InMemoryUsageMeter(Rates());

    /// <summary>The number of paid review calls one model pass makes: exactly one per fixture, no retries beyond the client's own.</summary>
    public static int CallsPerModel => SeededDraft.Ids().Count;

    /// <summary>Runs one model over the frozen set at most once per process; repeat calls return the cached pass.</summary>
    /// <param name="model">The model to review with.</param>
    /// <param name="ct">Cancels the pass.</param>
    /// <returns>The pass outcome.</returns>
    public Task<AcceptanceModelRun> RunAsync(string model, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        lock (_gate)
        {
            if (!_runs.TryGetValue(model, out var cached))
            {
                cached = Guard(model, ct);
                _runs[model] = cached;
            }

            return cached;
        }
    }

    /// <summary>Wraps the pass so a synchronous failure surfaces as a faulted task, not as a throw from the caller.</summary>
    private async Task<AcceptanceModelRun> Guard(string model, CancellationToken ct) =>
        await RunCoreAsync(model, ct).ConfigureAwait(false);

    private async Task<AcceptanceModelRun> RunCoreAsync(string model, CancellationToken ct)
    {
        var cap = _capUsd ??= Cap();
        var drafts = SeededDraft.LoadAll();

        Console.WriteLine(
            FormattableString.Invariant(
                $"seeded-defect acceptance: model {model}, {CallsPerModel} paid review calls planned, USD cap {cap:0.00}."));

        // The cap is checked before the second model as well as before every call (T25).
        if (_meter.TotalUsd >= cap)
        {
            Console.WriteLine(FormattableString.Invariant($"USD cap reached before {model} ({_meter.TotalUsd:0.0000} >= {cap:0.00}); pass skipped."));
            return new AcceptanceModelRun(model, DateTimeOffset.Now, Partial: true, ReportPath: null, [], null);
        }

        using var provider = Compose(model, ApiKey());
        var catalog = provider.GetRequiredService<IPromptCatalog>();
        if (string.IsNullOrWhiteSpace(catalog.Release))
        {
            throw new InvalidOperationException("The prompt catalog loaded no release; the acceptance run needs the shipped prompts.");
        }

        var writer = provider.GetRequiredService<IStoryWriter>();
        var limits = PriceLimits.D4Defaults;
        var run = new RunContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ReservationId(Guid.NewGuid()),
            GenerationTrigger.System,
            Attempt: 1,
            PriceVersion: "acceptance");

        var reviews = new List<StoryReviewV1>(drafts.Count);
        var partial = false;

        foreach (var draft in drafts)
        {
            if (_meter.TotalUsd >= cap)
            {
                partial = true;
                break;
            }

            var input = draft.ToReviewInput();
            var request = new StoryReviewRequest(
                run,
                input.Draft,
                input.Language,
                input.Tier,
                LengthTargetResolver.Resolve(input.Tier, input.Language, limits),
                input.Topic,
                input.Facts,
                input.Bible,
                EarRuleQuestions.For(input.Language),
                StoryPresetKeys.For(input.Topic, input.Language));

            var result = await writer.ReviewAsync(request, ct).ConfigureAwait(false);
            if (result is not TextCallOk<StoryReviewV1> ok)
            {
                // A provider error (or a missing/invalid key) aborts before any report is written (§Failure handling).
                throw new InvalidOperationException(
                    $"The review call for fixture '{draft.Id}' with model '{model}' failed with {result.GetType().Name}.");
            }

            reviews.Add(ok.Draft);
        }

        var outcomes = Outcomes(drafts, reviews);
        var report = reviews.Count == 0 ? null : Measure(outcomes, drafts, reviews);
        var reportPath = report is null ? null : WriteReport(model, partial, outcomes, report);
        PrintRow(model, partial, outcomes.Count, report);

        return new AcceptanceModelRun(model, DateTimeOffset.Now, partial, reportPath, outcomes, report);
    }

    /// <summary>Scores the reviewed prefix of the set; <see langword="null"/> when the prefix cannot be scored (no clean draft or no seeded defect yet).</summary>
    private SeededDefectReport? Measure(
        List<AcceptanceDraftOutcome> outcomes,
        IReadOnlyList<SeededDraft> drafts,
        List<StoryReviewV1> reviews)
    {
        var scored = drafts.Take(outcomes.Count).ToArray();
        return outcomes.Count >= 2 && scored.Any(draft => draft.Clean) && scored.Any(draft => draft.SeedDefects.Count > 0)
            ? SeededDefectScorer.Score(scored, reviews, _meter.TotalUsd)
            : null;
    }

    private static string WriteReport(
        string model,
        bool partial,
        List<AcceptanceDraftOutcome> outcomes,
        SeededDefectReport report)
    {
        var directory = Directory.CreateDirectory(Path.Combine(RepoRoot(), "trial", "review"));
        var path = Path.Combine(directory.FullName, $"acceptance-{model}-{DateTimeOffset.Now:yyyyMMdd-HHmm}.json");
        var document = new AcceptanceReportFile(model, DateTimeOffset.Now, partial, CallsPerModel, Cap(), outcomes, report);
        File.WriteAllText(path, JsonSerializer.Serialize(document, ReportOptions));

        return path;
    }

    private static List<AcceptanceDraftOutcome> Outcomes(IReadOnlyList<SeededDraft> drafts, List<StoryReviewV1> reviews)
    {
        var outcomes = new List<AcceptanceDraftOutcome>(reviews.Count);
        for (var i = 0; i < reviews.Count; i++)
        {
            var draft = drafts[i];
            var review = reviews[i];
            var detected = draft.SeedDefects
                .Where(defect => review.Issues.Any(issue => issue.Type == defect.Type && issue.Segment == defect.Segment))
                .Select(defect => new DefectRef(defect.Type, defect.Segment))
                .ToArray();
            var blocking = draft.Clean
                ? review.Issues
                    .Where(issue => issue.Severity == "blocking")
                    .Select(issue => issue.Type)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(type => type, StringComparer.Ordinal)
                    .ToArray()
                : [];

            outcomes.Add(new AcceptanceDraftOutcome(
                draft.Id,
                [.. draft.SeedDefects.Select(defect => new DefectRef(defect.Type, defect.Segment))],
                detected,
                blocking));
        }

        return outcomes;
    }

    private void PrintRow(string model, bool partial, int drafts, SeededDefectReport? report)
    {
        Console.WriteLine();
        Console.WriteLine("| model | recall | false-block | cost usd | drafts reviewed | partial |");
        Console.WriteLine("|---|---|---|---|---|---|");
        Console.WriteLine(
            FormattableString.Invariant(
                $"| {model} | {Number(report?.Recall)} | {Number(report?.FalseBlockRate)} | {_meter.TotalUsd:0.0000} | {drafts} | {partial} |"));

        if (report is { } measured)
        {
            Console.WriteLine();
            Console.WriteLine("| issue type | recall |");
            Console.WriteLine("|---|---|");
            foreach (var pair in measured.RecallByType.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Console.WriteLine(FormattableString.Invariant($"| {pair.Key} | {Number(pair.Value)} |"));
            }
        }

        Console.WriteLine();
    }

    private static string Number(double? value) =>
        value is { } measured ? measured.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";

    private ServiceProvider Compose(string model, string apiKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IUsageMeter>(_meter);
        services.AddSingleton<IStoryCostEstimator>(new ApproximateCostEstimator(model));
        services.AddSingleton<IFeatureFlags>(new AlwaysOnFeatureFlags());
        services.AddNightTalesApplication();
        services.AddOpenAiTextClient();
        services.AddPromptCatalog();
        services.Configure<OpenAiOptions>(options =>
        {
            options.ApiKey = apiKey;
            options.Models[TextStep.Review] = model;
        });

        return services.BuildServiceProvider();
    }

    private static PriceVersionRates Rates() => new(
        PricingOptions.DefaultModels.ToDictionary(
            pair => pair.Key,
            pair => new PriceRates(pair.Value.In, pair.Value.Cached, pair.Value.CacheWrite, pair.Value.Out),
            StringComparer.Ordinal),
        new Dictionary<string, ImagePriceRate>(StringComparer.Ordinal),
        WebSearchCallUsd: null);

    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(SeededDraft.FixtureDirectory(), "..", "..", ".."));

    private static decimal Cap()
    {
        var raw = Environment.GetEnvironmentVariable(MaxUsdEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultMaxUsd;
        }

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0m
            ? parsed
            : throw new InvalidOperationException(FormattableString.Invariant($"{MaxUsdEnvVar} must be a positive USD amount; read '{raw}'."));
    }

    private static string ApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var fromKeychain = Keychain().Trim();
        if (!string.IsNullOrWhiteSpace(fromKeychain))
        {
            return fromKeychain;
        }

        throw new InvalidOperationException(
            $"No provider key: set {ApiKeyEnvVar}, or store the story key in the '{KeychainService}' keychain item.");
    }

    private static string Keychain()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("security", ["find-generic-password", "-s", KeychainService, "-w"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return string.Empty;
            }

            var value = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? value : string.Empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private sealed record AcceptanceReportFile(
        string Model,
        DateTimeOffset GeneratedAt,
        bool Partial,
        int CallsPlanned,
        decimal MaxUsd,
        List<AcceptanceDraftOutcome> Drafts,
        SeededDefectReport? Report);

    /// <summary>Every feature flag on: the acceptance run never exercises a switched-off path.</summary>
    private sealed class AlwaysOnFeatureFlags : IFeatureFlags
    {
        public Task<bool> IsEnabledAsync(string key, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>
    /// The cheapest metering that stays faithful: each call is priced with the real <see cref="TextCostCalculator"/> at
    /// the shipped rates and accumulated in memory. Nothing is written to the database, so the <c>usage_records</c>
    /// cross-check needs the DB-backed variant documented on <see cref="SeededDefectRun"/>.
    /// </summary>
    private sealed class InMemoryUsageMeter(PriceVersionRates rates) : IUsageMeter
    {
        private readonly List<UsageItem> _items = [];

        public decimal TotalUsd { get; private set; }

        public IReadOnlyList<UsageItem> Items => _items;

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(item);
            _items.Add(item);

            var cost = TextCostCalculator.Compute(
                rates.RatesFor(item.Model),
                item.InputTokens,
                item.CachedInputTokens,
                item.CacheWriteTokens,
                item.OutputTokens,
                rates.SurchargeThresholdTokens);

            TotalUsd += cost ?? item.CostUsd ?? 0m;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Upper-bound stand-in for the pipeline's estimator: only reached when the provider reports no usage, because the
    /// real one needs the active price version and the persistence graph. It charges an assumed review input bound and
    /// the tier's <c>max_output_tokens</c> at the shipped rates of the model under test, so such a call is never priced
    /// at zero.
    /// </summary>
    private sealed class ApproximateCostEstimator(string model) : IStoryCostEstimator
    {
        private const int AssumedInputBytes = 40_000;

        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Bound(tier, language);

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Bound(tier, language);

        public Task<ErrorOr<UsageBound>> ReviewWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Bound(tier, language);

        public Task<ErrorOr<UsageBound>> RevisionWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Bound(tier, language);

        private Task<ErrorOr<UsageBound>> Bound(LengthTier tier, Language language)
        {
            var target = LengthTargetResolver.Resolve(tier, language, PriceLimits.D4Defaults);
            var rates = Rates();
            var cost = TextCostCalculator.Compute(
                rates.RatesFor(model),
                AssumedInputBytes,
                0,
                AssumedInputBytes,
                target.ReviewMaxOutput,
                rates.SurchargeThresholdTokens) ?? 0m;

            return Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(cost, AssumedInputBytes, target.ReviewMaxOutput));
        }
    }
}

/// <summary>The D20 skip gate, evaluated without touching any resource.</summary>
public static class LiveGate
{
    /// <summary>Whether the live acceptance set is enabled: <c>OPENAI_LIVE_TESTS=1</c> (02 README D20).</summary>
    public static bool OpenAiLive =>
        string.Equals(Environment.GetEnvironmentVariable(SeededDefectRun.LiveEnvVar), "1", StringComparison.Ordinal);

    /// <summary>The reason a skipped live test reports.</summary>
    public static string Reason =>
        $"{SeededDefectRun.LiveEnvVar}=1 is required; the seeded-defect acceptance set calls the real provider.";
}
