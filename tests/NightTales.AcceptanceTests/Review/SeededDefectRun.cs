using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <param name="Status">The scored outcome of the call: <c>ok</c>, a rejected review, or the non-Ok model outcome.</param>
/// <param name="Seeded">The planted defects.</param>
/// <param name="Detected">The planted defects an issue matched.</param>
/// <param name="BlockingOnClean">The blocking issue types on a clean draft, ordinal order; empty for a seeded draft.</param>
public sealed record AcceptanceDraftOutcome(
    string Id,
    string Status,
    IReadOnlyList<DefectRef> Seeded,
    IReadOnlyList<DefectRef> Detected,
    IReadOnlyList<string> BlockingOnClean);

/// <summary>The outcome of one model's pass over the frozen set (02.6.1-T2).</summary>
/// <param name="Model">The model under test.</param>
/// <param name="GeneratedAt">When the pass finished; the report name and its <c>generated_at</c> carry this one stamp.</param>
/// <param name="Partial"><see langword="true"/> when the USD cap stopped the pass before every draft was reviewed.</param>
/// <param name="ReportPath">The written report; every pass writes one, a partial pass with <c>partial: true</c>.</param>
/// <param name="PassCostUsd">This pass's own spend: the cumulative meter at the pass end minus its value at the pass start.</param>
/// <param name="SpentUsd">The cumulative meter at the pass end, across every pass in the process.</param>
/// <param name="CapUsd">The one cumulative USD cap that bounds all passes of the process.</param>
/// <param name="StatusCounts">The per-status draft counts of this pass.</param>
/// <param name="Drafts">Per-draft outcomes; only the drafts actually reviewed.</param>
/// <param name="Report">The measured report, or <see langword="null"/> when the reviewed prefix could not be scored.</param>
/// <param name="ConsoleLines">Every console line this pass printed, so D19 can be checked over the whole output.</param>
public sealed record AcceptanceModelRun(
    string Model,
    DateTimeOffset GeneratedAt,
    bool Partial,
    string ReportPath,
    decimal PassCostUsd,
    decimal SpentUsd,
    decimal CapUsd,
    IReadOnlyDictionary<string, int> StatusCounts,
    List<AcceptanceDraftOutcome> Drafts,
    SeededDefectReport? Report,
    IReadOnlyList<string> ConsoleLines);

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
/// <b>Key.</b> The only key source is the macOS keychain item the repository already uses for the story key
/// (<c>nts.openai.story</c>), read with <c>security find-generic-password -a "$USER" -s … -w</c> under a 10 s
/// timeout; the value is handed to the client and never logged, printed or written to a report. There is no
/// environment-variable source.
/// </para>
/// <para>
/// <b>Cap.</b> The USD cap is <b>one cumulative cap for both models</b>, not a per-pass budget: it is checked against
/// the shared meter's cumulative spend plus the worst-case bound of the next call, so a single call can never push
/// the run past the cap. A pass the cap stops is <c>partial</c> and decides nothing. Residual: the client may issue
/// up to <c>MaxAttempts = 2</c> HTTP requests per fixture and a billed first attempt is not metered before the
/// retry, so a pass can exceed the cap by at most the bound of one call; the cap itself cannot be exceeded by a
/// single call.
/// </para>
/// <para>
/// <b>Scoring.</b> After every Ok call the review is checked with the Domain <see cref="ReviewResultValidator"/>
/// exactly as <c>StoryGenerationPipeline</c> does, and the validated review is scored, so a taste issue is
/// downgraded and a missing ear-rule answer becomes an issue before recall is measured. There is no retry: the paid
/// bound stays one <c>ReviewAsync</c> call per fixture per model. A review the validator rejects is recorded as
/// <c>invalid_review</c> and scored as no issues (a miss for a seeded defect, no block on a clean draft); a
/// <c>InvalidOutput</c> or <c>Moderated</c> outcome is recorded as <c>invalid_output</c>/<c>moderated</c> and scored
/// the same way, so an earlier paid review is never discarded. Only a provider, transport, key or alias failure
/// aborts, before any report, with the spent USD and call count in the message.
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

    /// <summary>The environment variable overriding the USD cap (T25).</summary>
    public const string MaxUsdEnvVar = "NTS_ACCEPTANCE_MAX_USD";

    /// <summary>The default hard stop for the whole run, in USD (T25).</summary>
    public const decimal DefaultMaxUsd = 5m;

    /// <summary>The cheapest candidate model (the shipped <c>OpenAi:Models:Review</c>).</summary>
    public const string LunaModel = "gpt-6-luna";

    /// <summary>The stronger candidate model.</summary>
    public const string SolModel = "gpt-6-sol";

    /// <summary>The macOS keychain item the story key lives in (same service the worker scripts use).</summary>
    public const string KeychainService = "nts.openai.story";

    /// <summary>How long the keychain read may take before the pass aborts.</summary>
    public static readonly TimeSpan KeychainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The scored status of an Ok review the validator accepted.</summary>
    public const string StatusOk = "ok";

    /// <summary>The scored status of a review the <see cref="ReviewResultValidator"/> rejected.</summary>
    public const string StatusInvalidReview = "invalid_review";

    /// <summary>The scored status of a <c>TextCallInvalidOutput</c> outcome.</summary>
    public const string StatusInvalidOutput = "invalid_output";

    /// <summary>The scored status of a <c>TextCallModerated</c> outcome.</summary>
    public const string StatusModerated = "moderated";

    /// <summary>A review that carries no issue, so a draft with a non-Ok outcome scores as a miss and never blocks a clean draft.</summary>
    private static readonly StoryReviewV1 EmptyReview =
        new(new ReviewScores(3, 3, 3, 3, 3, null, 3), [], [], null, [], string.Empty);

    private static readonly Regex ModelNamePattern = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

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

    /// <summary>Wraps the pass so an asynchronous failure surfaces as a faulted task, not as a throw from the caller.</summary>
    private async Task<AcceptanceModelRun> Guard(string model, CancellationToken ct) =>
        await RunCoreAsync(model, ct).ConfigureAwait(false);

    private async Task<AcceptanceModelRun> RunCoreAsync(string model, CancellationToken ct)
    {
        var cap = _capUsd ??= Cap();
        var drafts = SeededDraft.LoadAll();
        var lines = new List<string>();
        IStoryCostEstimator estimator = new ApproximateCostEstimator(model);

        Log(lines, FormattableString.Invariant(
            $"seeded-defect acceptance: model {model}, {CallsPerModel} paid review calls planned, USD cap {cap:0.00} (one cumulative cap for both models)."));

        // Frozen-set guard (T27): the loaded fixtures must be exactly the frozen manifest, or a paid run would
        // score a set nobody froze. Checked before the first paid call, and before the key is read.
        GuardFrozenSet(drafts);

        var firstBound = await estimator.ReviewWorstCaseAsync(drafts[0].Tier, drafts[0].Language, ct).ConfigureAwait(false);
        if (OverCap(_meter.TotalUsd, cap, firstBound))
        {
            Log(lines, FormattableString.Invariant(
                $"USD cap reached before {model} (spent {_meter.TotalUsd:0.0000} + worst case {Bound(firstBound):0.0000} >= {cap:0.00}); pass skipped, no decision is recorded."));
            return Skipped(model, cap, _meter.TotalUsd, lines);
        }

        using var provider = Compose(model, ApiKey(), estimator);
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

        var startUsd = _meter.TotalUsd;
        var effective = new List<StoryReviewV1>(drafts.Count);
        var statuses = new List<string>(drafts.Count);
        var partial = false;

        foreach (var draft in drafts)
        {
            var bound = await estimator.ReviewWorstCaseAsync(draft.Tier, draft.Language, ct).ConfigureAwait(false);
            if (OverCap(_meter.TotalUsd, cap, bound))
            {
                partial = true;
                Log(lines, FormattableString.Invariant(
                    $"USD cap reached at {_meter.TotalUsd:0.0000} of {cap:0.00} after {effective.Count} of {CallsPerModel} drafts; the pass is partial and no decision is recorded."));
                break;
            }

            var input = draft.ToReviewInput();
            var questions = EarRuleQuestions.For(input.Language);
            var request = new StoryReviewRequest(
                run,
                input.Draft,
                input.Language,
                input.Tier,
                LengthTargetResolver.Resolve(input.Tier, input.Language, limits),
                input.Topic,
                input.Facts,
                input.Bible,
                questions,
                StoryPresetKeys.For(input.Topic, input.Language));

            var result = await writer.ReviewAsync(request, ct).ConfigureAwait(false);
            switch (result)
            {
                case TextCallOk<StoryReviewV1> ok:
                    // Exactly as StoryGenerationPipeline: validate the review before scoring it, so a taste issue is
                    // downgraded and a missing ear-rule answer becomes an issue. No retry: one paid call per fixture.
                    var questionIds = questions.Select(question => question.QuestionId).ToArray();
                    var validated = ReviewResultValidator.Validate(ok.Draft, input.Topic, input.Draft.Paragraphs.Count, questionIds);
                    if (validated.IsAccepted)
                    {
                        effective.Add(validated.Review);
                        statuses.Add(StatusOk);
                    }
                    else
                    {
                        // A rejected review scores as no issues: a miss for a seeded defect, no block on a clean draft.
                        effective.Add(EmptyReview);
                        statuses.Add(StatusInvalidReview);
                    }

                    break;

                case TextCallInvalidOutput<StoryReviewV1>:
                    // Billed, non-Ok: never throw, never discard an earlier review; score it as no issues and go on.
                    effective.Add(EmptyReview);
                    statuses.Add(StatusInvalidOutput);
                    break;

                case TextCallModerated<StoryReviewV1>:
                    effective.Add(EmptyReview);
                    statuses.Add(StatusModerated);
                    break;

                default:
                    // Provider, transport, key or alias failure (also funds-exhausted): abort before any report,
                    // naming the spent USD and call count so a failed pass never hides what it already cost.
                    throw Abort(model, draft.Id, result, _meter);
            }
        }

        var passCostUsd = _meter.TotalUsd - startUsd;
        var statusCounts = Counts(statuses);
        var outcomes = Outcomes(drafts, effective, statuses);
        var report = Measure(outcomes, drafts, effective, passCostUsd);
        var stamp = DateTimeOffset.Now;
        var reportPath = WriteReport(model, stamp, partial, passCostUsd, cap, outcomes, statusCounts, report);
        PrintRow(lines, model, partial, outcomes.Count, passCostUsd, statusCounts, report);

        return new AcceptanceModelRun(
            model,
            stamp,
            partial,
            reportPath,
            passCostUsd,
            _meter.TotalUsd,
            cap,
            statusCounts,
            outcomes,
            report,
            lines);
    }

    /// <summary>A pass that never ran a call: it writes an empty report with <c>partial: true</c> and decides nothing.</summary>
    private static AcceptanceModelRun Skipped(string model, decimal cap, decimal spentUsd, List<string> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stamp = DateTimeOffset.Now;
        var path = WriteReport(model, stamp, true, 0m, cap, [], counts, null);

        return new AcceptanceModelRun(model, stamp, true, path, 0m, spentUsd, cap, counts, [], null, lines);
    }

    /// <summary>Scores the reviewed prefix of the set; <see langword="null"/> when the prefix cannot be scored (no clean draft or no seeded defect yet).</summary>
    private static SeededDefectReport? Measure(
        List<AcceptanceDraftOutcome> outcomes,
        IReadOnlyList<SeededDraft> drafts,
        List<StoryReviewV1> reviews,
        decimal passCostUsd)
    {
        var scored = drafts.Take(outcomes.Count).ToArray();
        return outcomes.Count >= 2 && scored.Any(draft => draft.Clean) && scored.Any(draft => draft.SeedDefects.Count > 0)
            ? SeededDefectScorer.Score(scored, reviews, passCostUsd)
            : null;
    }

    private static List<AcceptanceDraftOutcome> Outcomes(
        IReadOnlyList<SeededDraft> drafts,
        List<StoryReviewV1> reviews,
        List<string> statuses)
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
                    .Where(issue => issue.Severity == SeededDefectScorer.Blocking)
                    .Select(issue => issue.Type)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(type => type, StringComparer.Ordinal)
                    .ToArray()
                : [];

            outcomes.Add(new AcceptanceDraftOutcome(
                draft.Id,
                statuses[i],
                [.. draft.SeedDefects.Select(defect => new DefectRef(defect.Type, defect.Segment))],
                detected,
                blocking));
        }

        return outcomes;
    }

    private static void PrintRow(
        List<string> lines,
        string model,
        bool partial,
        int drafts,
        decimal passCostUsd,
        IReadOnlyDictionary<string, int> statusCounts,
        SeededDefectReport? report)
    {
        Log(lines, string.Empty);
        Log(lines, "| model | recall | false-block | pass cost usd | drafts reviewed | partial |");
        Log(lines, "|---|---|---|---|---|---|");
        Log(lines, FormattableString.Invariant(
            $"| {model} | {Number(report?.Recall)} | {Number(report?.FalseBlockRate)} | {passCostUsd:0.0000} | {drafts} | {partial} |"));

        if (report is { } measured)
        {
            Log(lines, string.Empty);
            Log(lines, "| issue type | recall |");
            Log(lines, "|---|---|");
            foreach (var pair in measured.RecallByType.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Log(lines, FormattableString.Invariant($"| {pair.Key} | {Number(pair.Value)} |"));
            }
        }

        Log(lines, string.Empty);
        Log(lines, "| status | count |");
        Log(lines, "|---|---|");
        foreach (var pair in statusCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Log(lines, FormattableString.Invariant($"| {pair.Key} | {pair.Value} |"));
        }

        Log(lines, string.Empty);
    }

    private static string Number(double? value) =>
        value is { } measured ? measured.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>Writes one console line and keeps it, so a test can check D19 over the whole captured output.</summary>
    private static void Log(List<string> lines, string text)
    {
        Console.WriteLine(text);
        lines.Add(text);
    }

    private ServiceProvider Compose(string model, string apiKey, IStoryCostEstimator estimator)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IUsageMeter>(_meter);
        services.AddSingleton(estimator);
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

    /// <summary>The keychain read, with a hard timeout; returns the raw value or an empty string, never throws for a missing item.</summary>
    /// <exception cref="InvalidOperationException">The <c>security</c> tool cannot run or the read times out.</exception>
    private static string Keychain()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("security", ["find-generic-password", "-a", Environment.UserName, "-s", KeychainService, "-w"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The macOS 'security' tool reported no process.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The macOS 'security' tool is unavailable ({exception.GetType().Name}); the acceptance run needs the story key in the '{KeychainService}' keychain item.",
                exception);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(KeychainTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process already exited between the timeout and the kill; nothing to clean up.
            }

            throw new InvalidOperationException(
                FormattableString.Invariant($"Reading the '{KeychainService}' keychain item timed out after {KeychainTimeout.TotalSeconds:0} s; the acceptance run needs that key."));
        }

        return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : string.Empty;
    }

    private static string ApiKey()
    {
        var fromKeychain = Keychain().Trim();
        if (!string.IsNullOrWhiteSpace(fromKeychain))
        {
            return fromKeychain;
        }

        throw new InvalidOperationException(
            $"No provider key: store the story key in the '{KeychainService}' keychain item (security find-generic-password -a \"$USER\" -s {KeychainService} -w).");
    }

    /// <summary>Proves the loaded fixtures are exactly the frozen manifest before the first paid call (T27).</summary>
    /// <exception cref="InvalidOperationException">A draft was added, removed or renamed since the set was frozen.</exception>
    private static void GuardFrozenSet(IReadOnlyList<SeededDraft> drafts)
    {
        var loaded = drafts.Select(draft => draft.Id).ToHashSet(StringComparer.Ordinal);
        var frozen = SeededDefectManifest.Load().Ids.ToHashSet(StringComparer.Ordinal);

        var extra = loaded.Except(frozen).Order(StringComparer.Ordinal).ToArray();
        var missing = frozen.Except(loaded).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length == 0 && missing.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The seeded-defect set drifted from the frozen manifest: extra ids [{string.Join(", ", extra)}], missing ids [{string.Join(", ", missing)}]; re-freeze the manifest before a paid run.");
    }

    /// <summary>Whether the next call could push the cumulative spend past the cap: spent + the call's worst case.</summary>
    private static bool OverCap(decimal spentUsd, decimal capUsd, ErrorOr<UsageBound> bound) =>
        spentUsd + Bound(bound) >= capUsd;

    private static decimal Bound(ErrorOr<UsageBound> bound) =>
        bound.IsError ? 0m : bound.Value.WorstCaseUsd;

    private static Dictionary<string, int> Counts(List<string> statuses)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            counts[status] = counts.GetValueOrDefault(status) + 1;
        }

        return counts;
    }

    private static string WriteReport(
        string model,
        DateTimeOffset stamp,
        bool partial,
        decimal passCostUsd,
        decimal capUsd,
        List<AcceptanceDraftOutcome> outcomes,
        IReadOnlyDictionary<string, int> statusCounts,
        SeededDefectReport? report)
    {
        var eligible = SeededModelDecision.IsEligible(new SeededModelPass(report, partial));
        var document = new AcceptanceReportFile(
            model,
            stamp,
            partial,
            CallsPerModel,
            capUsd,
            passCostUsd,
            SeededModelDecision.RecallThreshold,
            SeededModelDecision.FalseBlockThreshold,
            eligible,
            statusCounts,
            outcomes,
            report);

        var directory = Directory.CreateDirectory(Path.Combine(RepoRoot(), "trial", "review"));
        var baseName = $"acceptance-{GuardedModel(model)}-{stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

        return CreateNewFile(directory.FullName, baseName, JsonSerializer.Serialize(document, ReportOptions));
    }

    /// <summary>Rejects a model name that is not a safe report-file segment (no path separators, no <c>..</c>).</summary>
    private static string GuardedModel(string model)
    {
        if (!ModelNamePattern.IsMatch(model) || model.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The model name '{model}' is not a safe report file segment (must match ^[A-Za-z0-9._-]+$ and contain no '..').");
        }

        return model;
    }

    /// <summary>Creates the report with <c>FileMode.CreateNew</c>; a same-second collision gets a numeric suffix and never overwrites a paid report.</summary>
    private static string CreateNewFile(string directory, string baseName, string contents)
    {
        for (var suffix = 0; ; suffix++)
        {
            var path = Path.Combine(directory, suffix == 0 ? baseName + ".json" : FormattableString.Invariant($"{baseName}-{suffix}.json"));
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(contents);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // A same-second collision: try the next suffix.
            }
        }
    }

    private static InvalidOperationException Abort(string model, string fixtureId, TextCallResult<StoryReviewV1> result, InMemoryUsageMeter meter)
    {
        var outcome = result.GetType().Name.Split('`')[0];
        return new InvalidOperationException(FormattableString.Invariant(
            $"The review call for fixture '{fixtureId}' with model '{model}' failed with {outcome}; the pass aborted after USD {meter.TotalUsd:0.0000} across {meter.Items.Count} recorded call(s); no report was written and no decision is recorded."));
    }

    private sealed record AcceptanceReportFile(
        string Model,
        DateTimeOffset GeneratedAt,
        bool Partial,
        int CallsPlanned,
        decimal MaxUsd,
        decimal PassCostUsd,
        double RecallThreshold,
        double FalseBlockThreshold,
        bool Eligible,
        IReadOnlyDictionary<string, int> StatusCounts,
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

        public List<UsageItem> Items => _items;

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(item);
            _items.Add(item);

            TotalUsd += Cost(item);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Mirrors <c>UsageMeter.CostOfAsync</c>: a call with no reported usage, or a provider miscount
        /// (<c>cached &gt; input</c>), or an unpriced model is settled at the caller's bound instead of throwing, so a
        /// paid call never looks free and the run never dies on a mispriced model.
        /// </summary>
        private decimal Cost(UsageItem item)
        {
            if (item.InputTokens is null || item.Estimated)
            {
                return item.CostUsd ?? 0m;
            }

            var miscounted = (item.CachedInputTokens ?? 0) > item.InputTokens;
            if (miscounted || !rates.Text.ContainsKey(item.Model))
            {
                return item.CostUsd ?? 0m;
            }

            return TextCostCalculator.Compute(
                rates.RatesFor(item.Model),
                item.InputTokens,
                item.CachedInputTokens,
                item.CacheWriteTokens,
                item.OutputTokens,
                rates.SurchargeThresholdTokens)
                ?? item.CostUsd
                ?? 0m;
        }
    }

    /// <summary>
    /// Upper-bound stand-in for the pipeline's estimator: only reached when the provider reports no usage, because the
    /// real one needs the active price version and the persistence graph. It charges an assumed review input bound and
    /// the tier's <c>max_output_tokens</c> at the shipped rates of the model under test, so such a call is never priced
    /// at zero, and the harness reuses the same bound as the unmetered-retry margin of the cap.
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
