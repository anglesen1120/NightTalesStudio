using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Usage;

/// <summary>
/// <c>IStoryCostEstimator</c> (02 README D14/CR-6, 02.2.4-T1): the worst-case bound the budget guard reserves
/// is the whole pipeline - writer, review, revision and review #2 at each step's <c>max_output_tokens</c>,
/// with UTF-8 byte-counted input - plus two free moderation calls, read from the active price version.
/// </summary>
public sealed class StoryCostEstimatorTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

    [Fact]
    public async Task StoryCostEstimator_WorstCase_SumsWriterReviewRevisionAndReview2()
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _);

        var bound = await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        var limit = PriceLimits.D4Defaults.Story[LengthTier.Short][Language.En];
        var expected =
            Cost(prompts.WriterBytes, limit.WriterMaxOutput)
            + Cost(prompts.ReviewBytes, limit.ReviewMaxOutput)
            + Cost(prompts.RevisionBytes, limit.RevisionMaxOutput)
            + Cost(prompts.ReviewBytes, limit.ReviewMaxOutput);
        bound.WorstCaseUsd.ShouldBe(expected, "four paid calls; review #2 reuses the review cap");
        bound.MaxOutputTokens.ShouldBe(limit.WriterMaxOutput);
    }

    [Fact]
    public async Task StoryCostEstimator_WorstCase_IncludesTwoZeroCostModerationCalls()
    {
        // The price version has no moderation model at all: the two free calls add 0 and need no rate.
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _);

        var bound = await sut.WorstCaseAsync(LengthTier.Standard, Language.Vi, TestContext.Current.CancellationToken);

        var limit = PriceLimits.D4Defaults.Story[LengthTier.Standard][Language.Vi];
        var paid =
            Cost(prompts.WriterBytes, limit.WriterMaxOutput)
            + Cost(prompts.ReviewBytes, limit.ReviewMaxOutput) * 2
            + Cost(prompts.RevisionBytes, limit.RevisionMaxOutput);
        bound.WorstCaseUsd.ShouldBe(paid, "moderation contributes exactly 0");
    }

    [Fact]
    public async Task StoryCostEstimator_WorstCase_IsAtLeastActualCostForARevisedRun()
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _);
        var ct = TestContext.Current.CancellationToken;

        foreach (var tier in Enum.GetValues<LengthTier>())
        {
            var bound = await sut.WorstCaseAsync(tier, Language.En, ct);
            var limit = PriceLimits.D4Defaults.Story[tier][Language.En];

            // A revised run at its caps with fewer input tokens than bytes (a token is never below one byte).
            var actual =
                Cost(prompts.WriterBytes / 4, limit.WriterMaxOutput)
                + Cost(prompts.ReviewBytes / 4, limit.ReviewMaxOutput)
                + Cost(prompts.RevisionBytes / 4, limit.RevisionMaxOutput)
                + Cost(prompts.ReviewBytes / 4, limit.ReviewMaxOutput);

            bound.WorstCaseUsd.ShouldBeGreaterThanOrEqualTo(actual, $"tier {tier}");
        }
    }

    [Fact]
    public async Task StoryCostEstimator_UsesActiveLimitsPerTier()
    {
        var sut = Estimator(new FakePrompts(), out var catalog);
        var ct = TestContext.Current.CancellationToken;

        var shortBound = await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct);
        var longBound = await sut.WorstCaseAsync(LengthTier.Long, Language.En, ct);
        longBound.MaxOutputTokens.ShouldBeGreaterThan(shortBound.MaxOutputTokens);
        longBound.WorstCaseUsd.ShouldBeGreaterThan(shortBound.WorstCaseUsd);

        // A re-calibrated band is a new price version: the next estimate reads it, no code change.
        catalog.Limits = new PriceLimits(new Dictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>>
        {
            [LengthTier.Short] = new Dictionary<Language, LengthLimit> { [Language.En] = new(1, 2, 1, 2, 100, 50, 60) },
        });
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).MaxOutputTokens.ShouldBe(100);
    }

    [Fact]
    public async Task StoryCostEstimator_InputBoundIsUtf8ByteLength()
    {
        // 10 two-byte characters: 20 bytes, 10 chars. The bound counts bytes (P6: bytes >= tokens).
        var prompts = new FakePrompts { RevisionPrompt = new string('é', 10) };
        var sut = Estimator(prompts, out _);

        var bound = await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        Encoding.UTF8.GetByteCount(prompts.RevisionPrompt).ShouldBe(20);
        bound.MaxInputTokens.ShouldBe(20 + (3 * StoryCostEstimator.DefaultMaxDataObjectBytes));
    }

    [Fact]
    public async Task StoryCostEstimator_InvalidatedWhenPromptReleaseChanges()
    {
        var prompts = new FakePrompts { Release = "2026.10.1", RevisionPrompt = "short" };
        var sut = Estimator(prompts, out _);
        var ct = TestContext.Current.CancellationToken;

        var before = await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct);

        // Same release: the cached measurement is served even though the text changed underneath.
        prompts.RevisionPrompt = new string('x', 5_000);
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).MaxInputTokens.ShouldBe(before.MaxInputTokens);

        // A new release re-measures.
        prompts.Release = "2026.10.2";
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).MaxInputTokens
            .ShouldBe(5_000 + (3 * StoryCostEstimator.DefaultMaxDataObjectBytes));
    }

    private static decimal Cost(int input, int output) =>
        TextCostCalculator.Compute(Luna, input, 0, 0, output).ShouldNotBeNull();

    private static StoryCostEstimator Estimator(FakePrompts prompts, out FakeCatalog catalog)
    {
        catalog = new FakeCatalog();

        return new StoryCostEstimator(
            catalog,
            prompts,
            Options.Create(new PricingOptions()),
            new ConfigurationBuilder().Build(),
            new FakeClock());
    }

    private sealed class FakeCatalog : IPriceCatalog
    {
        public PriceLimits Limits { get; set; } = PriceLimits.D4Defaults;

        public Task<PriceVersion> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) => Task.FromResult(Build());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => Task.FromResult(Build());

        private PriceVersion Build()
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { ["gpt-6-luna"] = Luna },
                new Dictionary<string, ImagePriceRate>(),
                null).ToJson();

            return PriceVersion.Create(1, FakeClock.DefaultStart, rates, PriceVersionJson.WriteLimits(Limits), "fake", FakeClock.DefaultStart);
        }
    }

    /// <summary>A catalog whose presets and shared blocks are empty, so the input bounds are known exactly.</summary>
    private sealed class FakePrompts : IPromptCatalog
    {
        public string Release { get; set; } = "2026.10.1";

        public string WriterPrompt { get; set; } = "writer prompt";

        public string ReviewPrompt { get; set; } = "review prompt";

        public string RevisionPrompt { get; set; } = "revision prompt";

        public int WriterBytes => Encoding.UTF8.GetByteCount(WriterPrompt) + StoryCostEstimator.DefaultMaxDataObjectBytes;

        public int ReviewBytes => Encoding.UTF8.GetByteCount(ReviewPrompt) + (2 * StoryCostEstimator.DefaultMaxDataObjectBytes);

        public int RevisionBytes => Encoding.UTF8.GetByteCount(RevisionPrompt) + (3 * StoryCostEstimator.DefaultMaxDataObjectBytes);

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } = new Dictionary<string, PromptFileInfo>();

        public string GetSystemPrompt() => string.Empty;

        public string GetPreset(Topic topic, Language language) => string.Empty;

        public string GetShared(PromptFile file, Language language) => string.Empty;

        public string GetStepPrompt(PromptStep promptStep) => promptStep switch
        {
            PromptStep.Writer => WriterPrompt,
            PromptStep.Review => ReviewPrompt,
            PromptStep.Revision => RevisionPrompt,
            _ => string.Empty,
        };

        public ReadOnlyMemory<byte> GetSchema(string name) => ReadOnlyMemory<byte>.Empty;

        public string BuildPromptVersion(PromptVersionParts parts) => Release;
    }
}
