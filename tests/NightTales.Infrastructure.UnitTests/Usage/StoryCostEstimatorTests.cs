using System.Text;
using Microsoft.Extensions.Configuration;
using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.Infrastructure.UnitTests.Prompts;
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

        var bound = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken)).Value;

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

        var bound = (await sut.WorstCaseAsync(LengthTier.Standard, Language.Vi, TestContext.Current.CancellationToken)).Value;

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
            var bound = (await sut.WorstCaseAsync(tier, Language.En, ct)).Value;
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

        var shortBound = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value;
        var longBound = (await sut.WorstCaseAsync(LengthTier.Long, Language.En, ct)).Value;
        longBound.MaxOutputTokens.ShouldBeGreaterThan(shortBound.MaxOutputTokens);
        longBound.WorstCaseUsd.ShouldBeGreaterThan(shortBound.WorstCaseUsd);

        // A re-calibrated band is a new price version: the next estimate reads it, no code change.
        catalog.Limits = new PriceLimits(new Dictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>>
        {
            [LengthTier.Short] = new Dictionary<Language, LengthLimit> { [Language.En] = new(1, 2, 1, 2, 100, 50, 60) },
        });
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value.MaxOutputTokens.ShouldBe(100);
    }

    [Fact]
    public async Task StoryCostEstimator_InputBoundIsUtf8ByteLength()
    {
        // 10 two-byte characters: 20 bytes, 10 chars. The bound counts bytes (P6: bytes >= tokens).
        var prompts = new FakePrompts { RevisionPrompt = new string('é', 10) };
        var sut = Estimator(prompts, out _);

        var bound = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken)).Value;

        Encoding.UTF8.GetByteCount(prompts.RevisionPrompt).ShouldBe(20);
        bound.MaxInputTokens.ShouldBe(20 + (3 * StoryCostEstimator.DefaultMaxDataObjectBytes));
    }

    [Fact]
    public async Task StoryCostEstimator_InvalidatedWhenPromptReleaseChanges()
    {
        var prompts = new FakePrompts { Release = "2026.10.1", RevisionPrompt = "short" };
        var sut = Estimator(prompts, out _);
        var ct = TestContext.Current.CancellationToken;

        var before = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value;

        // Same release: the cached measurement is served even though the text changed underneath.
        prompts.RevisionPrompt = new string('x', 5_000);
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value.MaxInputTokens.ShouldBe(before.MaxInputTokens);

        // A new release re-measures.
        prompts.Release = "2026.10.2";
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value.MaxInputTokens
            .ShouldBe(5_000 + (3 * StoryCostEstimator.DefaultMaxDataObjectBytes));
    }

    [Fact]
    public async Task StoryCostEstimator_DataBudgetChange_InvalidatesTheCachedMeasurement()
    {
        var prompts = new FakePrompts();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Story:Prompt:MaxDataObjectBytes"] = "1000" })
            .Build();
        var sut = new StoryCostEstimator(new FakeCatalog(), prompts, configuration, new FakeClock());
        var ct = TestContext.Current.CancellationToken;

        var small = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value;
        small.MaxInputTokens.ShouldBe(Encoding.UTF8.GetByteCount(prompts.RevisionPrompt) + 3_000);

        configuration["Story:Prompt:MaxDataObjectBytes"] = "2000";
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value.MaxInputTokens
            .ShouldBe(Encoding.UTF8.GetByteCount(prompts.RevisionPrompt) + 6_000);
    }

    [Fact]
    public async Task StoryCostEstimator_BoundIncludesCacheWrites_EvenInTheLongContextBranch()
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out var catalog);
        var ct = TestContext.Current.CancellationToken;
        var limit = PriceLimits.D4Defaults.Story[LengthTier.Short][Language.En];

        foreach (var threshold in new[] { TextCostCalculator.DefaultSurchargeThresholdTokens, 1_000 })
        {
            catalog.Threshold = threshold;
            var bound = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).Value;

            // A revised run in which every step wrote its whole prompt to the cache: the meter's own arithmetic.
            var settled =
                Cost(prompts.WriterBytes, limit.WriterMaxOutput, threshold)
                + Cost(prompts.ReviewBytes, limit.ReviewMaxOutput, threshold)
                + Cost(prompts.RevisionBytes, limit.RevisionMaxOutput, threshold)
                + Cost(prompts.ReviewBytes, limit.ReviewMaxOutput, threshold);
            bound.WorstCaseUsd.ShouldBeGreaterThanOrEqualTo(settled, $"threshold {threshold}");

            var withoutCacheWrite =
                TextCostCalculator.Compute(Luna, prompts.WriterBytes, 0, 0, limit.WriterMaxOutput, threshold).ShouldNotBeNull();
            (Cost(prompts.WriterBytes, limit.WriterMaxOutput, threshold) - withoutCacheWrite).ShouldBeGreaterThan(0m);
        }
    }

    [Fact]
    public async Task StoryCostEstimator_EmptyCatalog_ReturnsTheTypedError()
    {
        var sut = Estimator(new FakePrompts(), out var catalog);
        catalog.Empty = true;

        var result = await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }

    [Fact]
    public async Task StoryCostEstimator_StepPromptNotShipped_ReturnsTheTypedError()
    {
        // Every step prompt is required: without the revision prompt the whole-pipeline bound fails closed.
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "revise/v1.md");
        tree.Remove("revise/v1.md");
        var sut = Estimator(PromptsTree.Load(tree), out _);

        var result = await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);
    }

    // 02.6.1-T1 QA: ReviewWorstCaseAsync had no direct test (only a scripted fake in ReviewStepTests).
    [Fact]
    public async Task StoryCostEstimator_ReviewWorstCase_EmptyCatalog_ReturnsTheTypedError()
    {
        var sut = Estimator(new FakePrompts(), out var catalog);
        catalog.Empty = true;

        var result = await sut.ReviewWorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }

    [Fact]
    public async Task StoryCostEstimator_ReviewWorstCase_PricesExactlyOneReviewCallAtItsCap()
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _);
        var ct = TestContext.Current.CancellationToken;

        foreach (var tier in Enum.GetValues<LengthTier>())
        {
            var bound = (await sut.ReviewWorstCaseAsync(tier, Language.En, ct)).Value;
            var limit = PriceLimits.D4Defaults.Story[tier][Language.En];

            bound.MaxInputTokens.ShouldBe(prompts.ReviewBytes);
            bound.MaxOutputTokens.ShouldBe(limit.ReviewMaxOutput);
            bound.WorstCaseUsd.ShouldBe(Cost(prompts.ReviewBytes, limit.ReviewMaxOutput));

            // One review call is always cheaper than the whole pipeline it is part of.
            var whole = (await sut.WorstCaseAsync(tier, Language.En, ct)).Value;
            bound.WorstCaseUsd.ShouldBeLessThan(whole.WorstCaseUsd);
        }
    }

    [Fact]
    public async Task StoryCostEstimator_ReviewWorstCase_DoesNotNeedTheRevisionPrompt()
    {
        // A tree without the revision prompt: the review bound must still work while the whole-pipeline bound
        // keeps failing closed on the missing step prompt.
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "revise/v1.md");
        tree.Remove("revise/v1.md");
        var sut = Estimator(PromptsTree.Load(tree), out _);
        var ct = TestContext.Current.CancellationToken;

        var review = await sut.ReviewWorstCaseAsync(LengthTier.Short, Language.En, ct);

        review.IsError.ShouldBeFalse();
        review.Value.WorstCaseUsd.ShouldBeGreaterThan(0m);
        (await sut.WorstCaseAsync(LengthTier.Short, Language.En, ct)).FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);
    }

    [Fact]
    public async Task StoryCostEstimator_ReviewWorstCase_ReviewPromptNotShipped_ReturnsTheTypedError()
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "review/v1.md");
        tree.Remove("review/v1.md");
        var sut = Estimator(PromptsTree.Load(tree), out _);

        var result = await sut.ReviewWorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);
    }

    // 02.6.2-T1: the revision bound and the Story:Revision gate on the whole-pipeline reserve.
    [Fact]
    public async Task StoryCostEstimator_RevisionWorstCase_PricesExactlyOneRevisionCallAtItsCap()
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _);
        var ct = TestContext.Current.CancellationToken;

        foreach (var tier in Enum.GetValues<LengthTier>())
        {
            var bound = (await sut.RevisionWorstCaseAsync(tier, Language.En, ct)).Value;
            var limit = PriceLimits.D4Defaults.Story[tier][Language.En];

            bound.MaxInputTokens.ShouldBe(prompts.RevisionBytes);
            bound.MaxOutputTokens.ShouldBe(limit.RevisionMaxOutput);
            bound.WorstCaseUsd.ShouldBe(Cost(prompts.RevisionBytes, limit.RevisionMaxOutput));
        }
    }

    [Fact]
    public async Task StoryCostEstimator_RevisionWorstCase_EmptyCatalog_ReturnsTheTypedError()
    {
        var sut = Estimator(new FakePrompts(), out var catalog);
        catalog.Empty = true;

        var result = await sut.RevisionWorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }

    [Fact]
    public async Task StoryCostEstimator_RevisionWorstCase_RevisionPromptNotShipped_ReturnsTheTypedError()
    {
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "revise/v1.md");
        tree.Remove("revise/v1.md");
        var sut = Estimator(PromptsTree.Load(tree), out _);

        var result = await sut.RevisionWorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.PromptNotShipped);
    }

    [Fact]
    public async Task StoryCostEstimator_WorstCase_RevisionOn_ReservesTheRevisionAndItsReview2()
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _);
        var limit = PriceLimits.D4Defaults.Story[LengthTier.Short][Language.En];

        var bound = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken)).Value;

        bound.WorstCaseUsd.ShouldBe(
            Cost(prompts.WriterBytes, limit.WriterMaxOutput)
            + (Cost(prompts.ReviewBytes, limit.ReviewMaxOutput) * 2)
            + Cost(prompts.RevisionBytes, limit.RevisionMaxOutput));
    }

    [Theory]
    [InlineData("false", "1")]
    [InlineData("true", "0")]
    public async Task StoryCostEstimator_WorstCase_RevisionOff_ReservesOnlyTheWriterAndOneReview(
        string enabled, string maxRevisions)
    {
        var prompts = new FakePrompts();
        var sut = Estimator(prompts, out _, RevisionConfig(enabled, maxRevisions));
        var limit = PriceLimits.D4Defaults.Story[LengthTier.Short][Language.En];

        var bound = (await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken)).Value;

        bound.WorstCaseUsd.ShouldBe(
            Cost(prompts.WriterBytes, limit.WriterMaxOutput) + Cost(prompts.ReviewBytes, limit.ReviewMaxOutput),
            "a disabled revision (and its review #2) is never reserved for");
        bound.MaxInputTokens.ShouldBe(Math.Max(prompts.WriterBytes, prompts.ReviewBytes));
    }

    [Fact]
    public async Task StoryCostEstimator_WorstCase_RevisionOff_DoesNotNeedTheRevisionPrompt()
    {
        // A reserve that still counted the revision would fail closed without its prompt; with the revision off it
        // must compute from the writer and review prompts alone.
        var tree = PromptsTree.Read();
        PromptsTree.RemoveManifestEntry(tree, "revise/v1.md");
        tree.Remove("revise/v1.md");
        var sut = Estimator(PromptsTree.Load(tree), out _, RevisionConfig("false", "1"));

        var result = await sut.WorstCaseAsync(LengthTier.Short, Language.En, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Value.WorstCaseUsd.ShouldBeGreaterThan(0m);
    }

    // The bound charges a cache write of the whole input on top of the plain input (D14 example C).
    private static decimal Cost(int input, int output, int threshold = TextCostCalculator.DefaultSurchargeThresholdTokens) =>
        TextCostCalculator.Compute(Luna, input, 0, input, output, threshold).ShouldNotBeNull();

    private static StoryCostEstimator Estimator(
        IPromptCatalog prompts, out FakeCatalog catalog, IConfiguration? configuration = null)
    {
        catalog = new FakeCatalog();

        return new StoryCostEstimator(
            catalog,
            prompts,
            configuration ?? new ConfigurationBuilder().Build(),
            new FakeClock());
    }

    private static IConfiguration RevisionConfig(string enabled, string maxRevisions) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Story:Revision:Enabled"] = enabled,
                ["Story:Revision:MaxRevisions"] = maxRevisions,
            })
            .Build();

    private sealed class FakeCatalog : IPriceCatalog
    {
        public PriceLimits Limits { get; set; } = PriceLimits.D4Defaults;

        public bool Empty { get; set; }

        public int Threshold { get; set; } = TextCostCalculator.DefaultSurchargeThresholdTokens;

        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult(Empty
                ? (ErrorOr<PriceVersion>)Error.Failure(ErrorCodes.PriceCatalogEmpty, "empty")
                : Build());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => Task.FromResult(Build());

        private PriceVersion Build()
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { ["gpt-6-luna"] = Luna },
                new Dictionary<string, ImagePriceRate>(),
                null,
                Threshold).ToJson();

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

        public AttestationText GetAttestation(Language language) => new(string.Empty, string.Empty);

        public ReadOnlyMemory<byte> GetSchema(string name) => ReadOnlyMemory<byte>.Empty;

        public string BuildPromptVersion(PromptVersionParts parts) => Release;
    }
}
