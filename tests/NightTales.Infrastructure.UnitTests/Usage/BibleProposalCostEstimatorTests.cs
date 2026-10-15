using System.Text;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.Infrastructure.UnitTests.Prompts;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// 02.6.3-T2: <c>IBibleProposalCostEstimator</c> — the bound the budget guard reserves for one proposal run,
/// its output cap read from <c>price_versions.limits.bible_proposal</c> (README T56: never a configuration key)
/// and its input ceiling measured over the D6 data object.
/// </summary>
public sealed class BibleProposalCostEstimatorTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

    private static readonly PriceRates Sol = new(2.00m, 0.20m, 2.50m, 10.00m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorstCase_ReadsTheStepCapFromTheActivePriceVersion()
    {
        var catalog = new FakeCatalog();
        var sut = Estimator(new StubCatalog(), catalog);

        (await sut.WorstCaseAsync(Ct)).Value.MaxOutputTokens.ShouldBe(1_200);

        // A re-calibrated cap is a new price version: the next bound reads it, no code change (D14).
        catalog.Active = Version(2, bibleProposalMaxOutput: 900);

        (await sut.WorstCaseAsync(Ct)).Value.MaxOutputTokens.ShouldBe(900);
    }

    [Fact]
    public async Task WorstCase_CeilingsAreMeasuredOverTheDataObject_AndChargeACacheWrite()
    {
        var prompts = new StubCatalog();
        var sut = Estimator(prompts, new FakeCatalog());

        var bound = (await sut.WorstCaseAsync(Ct)).Value;

        // The bound's parts: the step prompt and the story body at six bytes per UTF-16 unit (the worst case a
        // JSON string can need), the bible at Bible:Limits:MaxContentBytes, and the episode's control values,
        // title and beats (the widest MaxSegments of the D4 table, 60) in the same worst case.
        var expectedBytes = (6L * (StubCatalog.Prompt.Length + 24_000)) + 65_536 + (6L * ((60 * 64) + 1_024));
        var expectedInput = (int)((expectedBytes + 3) / 4);

        bound.MaxInputTokens.ShouldBe(expectedInput);
        bound.MaxOutputTokens.ShouldBe(1_200);
        bound.WorstCaseUsd.ShouldBe(
            TextCostCalculator.Compute(Luna, expectedInput, 0, expectedInput, 1_200, TextCostCalculator.DefaultSurchargeThresholdTokens).ShouldNotBeNull(),
            "every input token is also charged a cache write (D14 example C)");
    }

    [Fact]
    public async Task WorstCase_IsAtLeastTheCostOfTheLargestPossibleCall()
    {
        // The real data object at its ceilings: a maximum body, a maximum bible block, the largest beat list.
        var body = new string('b', 24_000);
        var bible = new string('n', 65_000);
        var dataObject = Encoding.UTF8.GetByteCount(string.Concat(body, bible, new string('t', 60 * 8)));

        var sut = Estimator(new StubCatalog(), new FakeCatalog());
        var bound = (await sut.WorstCaseAsync(Ct)).Value;

        var actual = TextCostCalculator.Compute(Luna, dataObject / 4, 0, dataObject / 4, 1_200, TextCostCalculator.DefaultSurchargeThresholdTokens).ShouldNotBeNull();

        bound.WorstCaseUsd.ShouldBeGreaterThanOrEqualTo(actual);
    }

    [Fact]
    public async Task WorstCase_ModelFollowsTheBibleStepKey_AndPreventsAnUncostedCall()
    {
        var catalog = new FakeCatalog();
        catalog.Active = Version(1, luna: Sol, model: "gpt-6-sol");
        var configuration = Configuration("gpt-6-sol");
        var sut = Estimator(new StubCatalog(), catalog, configuration);

        var bound = (await sut.WorstCaseAsync(Ct)).Value;

        bound.WorstCaseUsd.ShouldBe(
            TextCostCalculator.Compute(Sol, bound.MaxInputTokens, 0, bound.MaxInputTokens, 1_200, TextCostCalculator.DefaultSurchargeThresholdTokens).ShouldNotBeNull());

        // A key naming a model the price version does not carry is a loud failure, not a silent zero price.
        await Should.ThrowAsync<UnknownModelPricingException>(async () =>
            await Estimator(new StubCatalog(), new FakeCatalog(), Configuration("gpt-6-unknown")).WorstCaseAsync(Ct));
    }

    [Fact]
    public async Task WorstCase_DefaultModel_IsTheAdapters()
    {
        var catalog = new FakeCatalog();

        // Both models are priced, at different rates: the default is the cheap one and `gpt-6-sol` the expensive
        // one, so a run priced at the version's other model instead of the adapter's default shows up as the
        // wrong cost here.
        catalog.Active = Version(
            1,
            luna: Luna,
            model: BibleProposalCostEstimator.DefaultModel,
            other: Sol,
            otherModel: "gpt-6-sol");
        var sut = Estimator(new StubCatalog(), catalog, Configuration(model: null));

        var result = await sut.WorstCaseAsync(Ct);

        // The default is the adapter's own: the value `OpenAiOptions` carries when `OpenAi:Models:Bible` is absent.
        BibleProposalCostEstimator.DefaultModel.ShouldBe(new OpenAiOptions().Models[TextStep.Bible]);
        result.IsError.ShouldBeFalse();
        result.Value.WorstCaseUsd.ShouldBe(
            TextCostCalculator.Compute(Luna, result.Value.MaxInputTokens, 0, result.Value.MaxInputTokens, 1_200, TextCostCalculator.DefaultSurchargeThresholdTokens).ShouldNotBeNull(),
            "an absent OpenAi:Models:Bible is priced at the adapter's default, never at the version's other model");
    }

    [Fact]
    public async Task WorstCase_EmptyCatalog_ReturnsTheTypedError()
    {
        var catalog = new FakeCatalog { Empty = true };

        var result = await Estimator(new StubCatalog(), catalog).WorstCaseAsync(Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }

    [Fact]
    public async Task WorstCase_ProposalPromptNotShipped_ReturnsTheTypedError()
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "bible/propose.v1.md");
        tree.Remove("bible/propose.v1.md");

        var result = await Estimator(PromptsTree.Load(tree), new FakeCatalog()).WorstCaseAsync(Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);
    }

    [Fact]
    public async Task WorstCase_StoryCeilingAndContentBudget_AreReadFromTheBibleSection()
    {
        var prompts = new StubCatalog();
        var small = Estimator(
            prompts,
            new FakeCatalog(),
            Configuration(model: null, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Bible:Proposal:MaxStoryChars"] = "100",
                ["Bible:Limits:MaxContentBytes"] = "1000",
            }));

        var bound = (await small.WorstCaseAsync(Ct)).Value;

        (await Estimator(prompts, new FakeCatalog()).WorstCaseAsync(Ct)).Value.MaxInputTokens
            .ShouldBeGreaterThan(bound.MaxInputTokens);
    }

    private static BibleProposalCostEstimator Estimator(IPromptCatalog prompts, FakeCatalog catalog, IConfiguration? configuration = null) =>
        new(catalog, prompts, configuration ?? Configuration(model: null), new FakeClock());

    private static IConfiguration Configuration(string? model, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (model is not null)
        {
            values["OpenAi:Models:Bible"] = model;
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static PriceVersion Version(
        int number,
        int bibleProposalMaxOutput = 1_200,
        PriceRates? luna = null,
        string model = "gpt-6-luna",
        PriceRates? other = null,
        string otherModel = "gpt-6-sol")
    {
        var activeFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var table = new Dictionary<string, PriceRates>(StringComparer.Ordinal) { [model] = luna ?? Luna };
        if (other is not null)
        {
            // A second priced model: what lets a test prove the estimator never prices its default with it.
            table[otherModel] = other;
        }

        var rates = new PriceVersionRates(
            table,
            new Dictionary<string, ImagePriceRate>(),
            null).ToJson();
        var limits = PriceVersionJson.WriteLimits(PriceLimits.D4Defaults with { BibleProposalMaxOutput = bibleProposalMaxOutput });

        return PriceVersion.Create(number, activeFrom, rates, limits, $"test-version-{number}", activeFrom);
    }

    /// <summary>The catalog of one active version, mutable so a re-price is a new row (D14).</summary>
    private sealed class FakeCatalog : IPriceCatalog
    {
        internal PriceVersion Active { get; set; } = Version(1);

        internal bool Empty { get; set; }

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult(
                Empty
                    ? Error.Failure(ErrorCodes.PriceCatalogEmpty, "no active price version")
                    : (ErrorOr<PriceVersion>)Active);

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A prompt catalog whose proposal prompt is one canned string; the real one is 02.2.3-T1's.</summary>
    private sealed class StubCatalog : IPromptCatalog
    {
        internal const string Prompt = "BIBLE-PROPOSAL-SYSTEM-PROMPT";

        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } = new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        public string GetSystemPrompt() => Prompt;

        public string GetPreset(Topic topic, Language language) => Prompt;

        public string GetShared(PromptFile file, Language language) => Prompt;

        public string GetStepPrompt(PromptStep promptStep) => Prompt;

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => Encoding.UTF8.GetBytes("{}");

        public string BuildPromptVersion(PromptVersionParts parts) => "rel=test";
    }
}
