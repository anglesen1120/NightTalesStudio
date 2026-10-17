using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Autopilot;

/// <summary>
/// 02.8.1-T1 plan §7.1: <c>IdeaCostEstimator</c> — the D1 bound of one idea call, its output ceiling read from
/// <c>price_versions.limits.ideas.max_output_tokens</c> of the run's own version (README T56: never a
/// configuration key) and its input ceiling measured over the step prompt and the data-object budget.
/// </summary>
public sealed class IdeaCostEstimatorTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

    private static readonly PriceRates Sol = new(2.00m, 0.20m, 2.50m, 10.00m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorstCase_ReadsTheIdeasCapFromTheRunPriceVersion()
    {
        var prices = new FakeCatalog();
        var sut = Estimator(prices);

        (await sut.WorstCaseAsync("1", Ct)).Value.MaxOutputTokens.ShouldBe(1_500);

        // A re-calibrated cap is a new price version: the run's own version is read, no code change (D14).
        prices.Rows[2] = Version(2, ideasMaxOutput: 900);

        (await sut.WorstCaseAsync("2", Ct)).Value.MaxOutputTokens.ShouldBe(900);
    }

    [Fact]
    public async Task WorstCase_CeilsTheInputOverTheStepPromptAndTheDataObjectBudget()
    {
        var prompts = new StubCatalog();
        var sut = Estimator(new FakeCatalog(), prompts);

        var bound = (await sut.WorstCaseAsync("1", Ct)).Value;

        // The bound's parts: the step prompt at six bytes per UTF-16 unit (the worst case a JSON string can need)
        // and the data object at the configured budget, four bytes per estimated token, rounded up.
        var expectedBytes = (6L * prompts.PromptLength) + PromptDataBudget.DefaultMaxDataObjectBytes;
        var expectedInput = (int)((expectedBytes + 3) / 4);

        bound.MaxInputTokens.ShouldBe(expectedInput);
        bound.MaxOutputTokens.ShouldBe(PriceLimits.DefaultIdeasMaxOutput);
        bound.WorstCaseUsd.ShouldBe(
            TextCostCalculator.Compute(Luna, expectedInput, 0, expectedInput, 1_500, TextCostCalculator.DefaultSurchargeThresholdTokens).ShouldNotBeNull(),
            "every input token is also charged a cache write (D14 example C)");
    }

    [Fact]
    public async Task WorstCase_ModelFollowsTheIdeasStepKey()
    {
        var prices = new FakeCatalog();
        // A second priced model: what lets a test prove the estimator prices the step, not its own default.
        prices.Rows[1] = Version(1, luna: Sol, model: "gpt-6-sol");

        var configured = new IdeaCostEstimator(prices, new StubCatalog(), Configuration(model: "gpt-6-sol"));
        var bound = (await configured.WorstCaseAsync("1", Ct)).Value;

        bound.WorstCaseUsd.ShouldBe(
            TextCostCalculator.Compute(Sol, bound.MaxInputTokens, 0, bound.MaxInputTokens, 1_500, TextCostCalculator.DefaultSurchargeThresholdTokens).ShouldNotBeNull());

        // A key naming a model the price version does not carry is a loud failure, not a silent zero price.
        await Should.ThrowAsync<UnknownModelPricingException>(async () =>
            await new IdeaCostEstimator(prices, new StubCatalog(), Configuration(model: "gpt-6-unknown")).WorstCaseAsync("1", Ct));
    }

    [Fact]
    public async Task WorstCase_DefaultModel_IsTheAdapters_AndIsNeverPricedWithAnotherModelsRates()
    {
        var prices = new FakeCatalog();
        var sut = Estimator(prices);

        sut.Model.ShouldBe(IdeaCostEstimator.DefaultModel);
        sut.Model.ShouldBe("gpt-6-luna");

        // Only a different model is priced, so the default one must stop the call rather than borrow its rates.
        prices.Rows[1] = Version(1, luna: Sol, model: "gpt-6-sol");

        await Should.ThrowAsync<UnknownModelPricingException>(async () => await sut.WorstCaseAsync("1", Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("7")]
    public async Task WorstCase_AnUnusableRunVersion_ReturnsTheTypedPriceCatalogEmptyError(string priceVersion)
    {
        // "7" is a well-formed number the catalog does not carry; the rest are not version numbers at all.
        var result = await Estimator(new FakeCatalog()).WorstCaseAsync(priceVersion, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }

    [Fact]
    public async Task WorstCase_StepPromptNotShipped_ReturnsTheTypedPromptNotShippedError()
    {
        var prompts = new StubCatalog { ThrowOnIdeasPrompt = true };

        var result = await Estimator(new FakeCatalog(), prompts).WorstCaseAsync("1", Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);
    }

    [Fact]
    public async Task WorstCase_AZeroIdeasCap_FailsClosedInsteadOfSendingZeroOutputTokens()
    {
        var prices = new FakeCatalog();
        prices.Rows[1] = Version(1, ideasMaxOutput: 0);

        await Should.ThrowAsync<InvalidOperationException>(async () => await Estimator(prices).WorstCaseAsync("1", Ct));
    }

    private static IdeaCostEstimator Estimator(FakeCatalog prices, IPromptCatalog? prompts = null, IConfiguration? configuration = null) =>
        new(prices, prompts ?? new StubCatalog(), configuration ?? Configuration());

    private static IConfiguration Configuration(string? model = null, int? maxDataObjectBytes = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (model is not null)
        {
            values[IdeaCostEstimator.ModelKey] = model;
        }

        if (maxDataObjectBytes is not null)
        {
            values[PromptDataBudget.ConfigurationKey] = maxDataObjectBytes.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static PriceVersion Version(
        int number,
        int ideasMaxOutput = PriceLimits.DefaultIdeasMaxOutput,
        PriceRates? luna = null,
        string model = "gpt-6-luna")
    {
        var activeFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates>(StringComparer.Ordinal) { [model] = luna ?? Luna },
            new Dictionary<string, ImagePriceRate>(),
            null).ToJson();
        var limits = PriceVersionJson.WriteLimits(PriceLimits.D4Defaults with { IdeasMaxOutput = ideasMaxOutput });

        return PriceVersion.Create(number, activeFrom, rates, limits, $"test-version-{number}", activeFrom);
    }

    /// <summary>The price rows the estimator addresses by the run's own version number.</summary>
    private sealed class FakeCatalog : IPriceCatalog
    {
        internal Dictionary<int, PriceVersion> Rows { get; } = new() { [1] = Version(1) };

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromException<ErrorOr<PriceVersion>>(new NotSupportedException());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) =>
            Rows.TryGetValue(version, out var row)
                ? Task.FromResult(row)
                : Task.FromException<PriceVersion>(new KeyNotFoundException($"no price version {version}"));
    }

    /// <summary>A prompt catalog whose idea step prompt is one canned string; the real one is 02.2.3-T1's.</summary>
    private sealed class StubCatalog : IPromptCatalog
    {
        internal const string Prompt = "IDEAS-SYSTEM-PROMPT";

        internal int PromptLength => GetStepPrompt(PromptStep.Ideas).Length;

        internal bool ThrowOnIdeasPrompt { get; init; }

        public string Release => "test";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } =
            new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        public string GetSystemPrompt() => Prompt;

        public string GetPreset(Topic topic, Language language) => Prompt;

        public string GetShared(PromptFile file, Language language) => Prompt;

        public string GetStepPrompt(PromptStep promptStep) =>
            ThrowOnIdeasPrompt
                ? throw new PromptCatalogException(["the manifest does not track 'ideas/generate.v1.md'"])
                : Prompt;

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => ReadOnlyMemory<byte>.Empty;

        public string BuildPromptVersion(PromptVersionParts parts) => "rel=test";
    }
}
