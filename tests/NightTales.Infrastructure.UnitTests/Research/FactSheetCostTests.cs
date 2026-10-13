using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.GenerateFactSheet;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// 02.5.3-T2 (P7): the start-up bounds of the fact-sheet cost options — the Api composes
/// <c>AddUsageMetering</c> without the extractor, so this section needs its own validator — and the worst-case
/// bound the estimator derives from them, which must never overflow.
/// </summary>
public sealed class FactSheetCostOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new FactSheetCostOptions();

        options.Model.ShouldBe(FactSheetCostEstimator.DefaultModel);
        options.MaxInputTokens.ShouldBe(FactInputLimits.DefaultMaxInputTokens);
        new FactSheetCostOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FactInputLimits.MaxMaxInputTokens + 1)]
    [InlineData(int.MaxValue)]
    public void MaxInputTokens_OutsideTheBound_FailsStartupValidation(int value)
    {
        var options = new FactSheetCostOptions { MaxInputTokens = value };

        var result = new FactSheetCostOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain(nameof(FactSheetCostOptions.MaxInputTokens));
    }

    [Fact]
    public void MaxInputTokens_AtTheBound_Passes()
    {
        var options = new FactSheetCostOptions { MaxInputTokens = FactInputLimits.MaxMaxInputTokens };

        new FactSheetCostOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Model_Missing_FailsStartupValidation(string model)
    {
        var options = new FactSheetCostOptions { Model = model };

        var result = new FactSheetCostOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain("Model");
    }
}

/// <summary>
/// <c>IFactSheetCostEstimator</c> (02 README D14/T56): the whole paid call of a fact-sheet run priced at the
/// version's rates, with the input charged as a cache write at four bytes per estimated token.
/// </summary>
public sealed class FactSheetCostEstimatorTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorstCase_BooksFourBytesPerEstimatedTokenAsInputAndTheRuledOutputCap()
    {
        var bound = (await Estimator(maxInputTokens: 60_000).WorstCaseAsync(Ct)).Value;

        bound.MaxInputTokens.ShouldBe(240_000, "the byte ceiling is four times the estimated-token ceiling");
        bound.MaxOutputTokens.ShouldBe(FactSheetCostEstimator.MaxOutputTokens);
        bound.WorstCaseUsd.ShouldBeGreaterThan(0m);
    }

    [Fact]
    public async Task WorstCase_SaturatesAVastConfiguredCeiling_InsteadOfOverflowing()
    {
        // P7: the bound's token count is an int, so 4 x a ceiling a host bypassed the start-up bounds with
        // cannot be allowed to throw on the POST path.
        var bound = (await Estimator(maxInputTokens: int.MaxValue).WorstCaseAsync(Ct)).Value;

        bound.MaxInputTokens.ShouldBe(int.MaxValue);
        bound.WorstCaseUsd.ShouldBeGreaterThan(0m);
    }

    private static FactSheetCostEstimator Estimator(int maxInputTokens) =>
        new(new FakeCatalog(), new FactSheetCostOptions { MaxInputTokens = maxInputTokens }, new FakeClock());

    /// <summary>A catalog with one active version whose only rate is the facts model's.</summary>
    private sealed class FakeCatalog : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(Build());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => Task.FromResult(Build());

        private static PriceVersion Build()
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { [FactSheetCostEstimator.DefaultModel] = Luna },
                new Dictionary<string, ImagePriceRate>(),
                null,
                TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();

            return PriceVersion.Create(
                1,
                FakeClock.DefaultStart,
                rates,
                PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
                "fake",
                FakeClock.DefaultStart);
        }
    }
}
