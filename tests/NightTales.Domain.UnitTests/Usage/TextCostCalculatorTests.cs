using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Usage;

/// <summary>
/// The cost formula of 02 README D14 (02.2.4-T1): worked examples A–D asserted to the exact six-decimal
/// value, the strict 272K surcharge guard, the null-usage and unknown-model contracts, rounding, and the
/// refusal of a provider miscount.
/// </summary>
public sealed class TextCostCalculatorTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);
    private static readonly PriceRates Sol = new(2m, 0.20m, 2.50m, 10m);

    [Fact]
    public void TextCostCalculator_WorkedExampleA_LunaNoCache_Returns0Point002700() =>
        TextCostCalculator.Compute(Luna, 12_000, 0, 0, 3_000).ShouldBe(0.002700m);

    [Fact]
    public void TextCostCalculator_WorkedExampleB_WithCachedTokens_Returns0Point001900() =>
        TextCostCalculator.Compute(Luna, 20_000, 15_000, 0, 2_500).ShouldBe(0.001900m);

    [Fact]
    public void TextCostCalculator_WorkedExampleC_SolWithCacheWrite_Returns0Point125000() =>
        TextCostCalculator.Compute(Sol, 30_000, 0, 10_000, 4_000).ShouldBe(0.125000m);

    [Fact]
    public void TextCostCalculator_InputOver272k_AppliesDoubleInputAndOnePointFiveOutput_Returns0Point063750() =>
        TextCostCalculator.Compute(Luna, 300_000, 0, 0, 5_000).ShouldBe(0.063750m);

    [Fact]
    public void TextCostCalculator_InputExactly272k_DoesNotSurcharge()
    {
        // Strictly "> 272K": at the threshold the plain rates apply, one token above the whole request doubles.
        TextCostCalculator.Compute(Luna, 272_000, 0, 0, 1_000).ShouldBe(0.027700m);
        TextCostCalculator.Compute(Luna, 272_001, 0, 0, 1_000).ShouldBe(0.055150m);
    }

    [Fact]
    public void TextCostCalculator_UsageAbsent_ReturnsNullCost() =>
        TextCostCalculator.Compute(Luna, null, null, null, null).ShouldBeNull();

    [Fact]
    public void TextCostCalculator_UnknownModel_ThrowsAtEstimateTime()
    {
        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = Luna },
            new Dictionary<string, ImagePriceRate>(),
            null);

        rates.RatesFor("gpt-6-luna").ShouldBe(Luna);
        Should.Throw<UnknownModelPricingException>(() => rates.RatesFor("gpt-9-unknown"))
            .Message.ShouldContain("gpt-9-unknown");
    }

    [Fact]
    public void TextCostCalculator_RoundsToSixDecimals()
    {
        // (0.10 + 0.125) / 1e6 = 0.000000225 → 0.000000; 4 × 0.125 / 1e6 = 0.0000005 → half away from zero → 0.000001.
        TextCostCalculator.Compute(Luna, 1, 0, 1, 0).ShouldBe(0m);
        TextCostCalculator.Compute(new PriceRates(0m, 0m, 0.125m, 0m), 0, 0, 4, 0).ShouldBe(0.000001m);

        var cost = TextCostCalculator.Compute(Luna, 12_345, 1_111, 7, 999).ShouldNotBeNull();
        decimal.Round(cost, 6).ShouldBe(cost, "the result never carries more than six decimals");
    }

    [Fact]
    public void TextCostCalculator_ReasoningTokensIncludedInOutputTokens()
    {
        // O4: reasoning tokens are part of output_tokens, so 500 visible + 2,500 reasoning bills as 3,000 output.
        TextCostCalculator.Compute(Luna, 12_000, 0, 0, 500 + 2_500)
            .ShouldBe(TextCostCalculator.Compute(Luna, 12_000, 0, 0, 3_000));
    }

    [Fact]
    public void TextCostCalculator_CachedTokensCannotExceedInput_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TextCostCalculator.Compute(Luna, 1_000, 1_001, 0, 0));
}
