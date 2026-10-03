using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Usage;

/// <summary>QA boundary cases of 02.2.4-T1 beyond the task's worked examples: zero, negative, huge, cache-write over the threshold.</summary>
public sealed class QaTextCostCalculatorBoundaryTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);
    private static readonly PriceRates Sol = new(2m, 0.20m, 2.50m, 10m);

    [Fact]
    public void Qa_ZeroTokensWithUsagePresent_CostsZeroNotNull() =>
        TextCostCalculator.Compute(Luna, 0, 0, 0, 0).ShouldBe(0m);

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(10, -1, 0, 0)]
    [InlineData(10, 0, -1, 0)]
    [InlineData(10, 0, 0, -1)]
    public void Qa_NegativeTokenCounts_AreRejected(long input, long cached, long write, long output) =>
        Should.Throw<ArgumentOutOfRangeException>(() => TextCostCalculator.Compute(Luna, input, cached, write, output));

    [Fact]
    public void Qa_CachedEqualsInput_IsAllowedAndPricedAtCachedRate() =>
        TextCostCalculator.Compute(Luna, 1_000_000, 1_000_000, 0, 0).ShouldBe(0.010000m);

    [Fact]
    public void Qa_VeryLargeTokenCounts_DoNotOverflowOrLosePrecision()
    {
        // 2^31-ish and beyond: long inputs, decimal arithmetic; surcharge branch applies (> 272K).
        // input 5e9 × 0.20 + output 3e9 × 0.75 = 1e9 + 2.25e9 = 3.25e9 per-million units → 3250.000000
        TextCostCalculator.Compute(Luna, 5_000_000_000L, 0, 0, 3_000_000_000L).ShouldBe(3250.000000m);
    }

    [Fact]
    public void Qa_CacheWriteOverThreshold_DoublesTheWriteRate()
    {
        // 300,000 input, 10,000 cache write on Sol: input 300000×4 + write 10000×5 + output 1000×15 = 1.2e6+5e4+1.5e4 = 1,265,000 /1e6
        TextCostCalculator.Compute(Sol, 300_000, 0, 10_000, 1_000).ShouldBe(1.265000m);
    }

    [Fact]
    public void Qa_CachedTokensAboveThreshold_StillBilledAtUnscaledCachedRate()
    {
        // The surcharge doubles input and write only; the cached rate is not scaled (O3 wording).
        // 300,000 input, 100,000 cached: 200000×0.20 + 100000×0.01 = 40000+1000 = 41,000 /1e6
        TextCostCalculator.Compute(Luna, 300_000, 100_000, 0, 0).ShouldBe(0.041000m);
    }

    [Fact]
    public void Qa_CustomThreshold_IsHonouredStrictlyAbove()
    {
        TextCostCalculator.Compute(Luna, 100, 0, 0, 0, surchargeThresholdTokens: 100).ShouldBe(0.000010m);
        TextCostCalculator.Compute(Luna, 101, 0, 0, 0, surchargeThresholdTokens: 100).ShouldBe(0.000020m);
    }

    [Fact]
    public void Qa_NullRates_Throws() =>
        Should.Throw<ArgumentNullException>(() => TextCostCalculator.Compute(null!, 1, 0, 0, 0));

    [Fact]
    public void Qa_PriceVersionRates_RoundTripAndKeyOrderCanonical()
    {
        var shuffled = """{"gpt-6-sol":{"out":10,"in":2,"cached":0.2,"cache_write":2.5}, "gpt-6-luna":{"in":0.10,"cached":0.01,"cache_write":0.125,"out":0.50}}""";
        var reordered = """{"gpt-6-luna":{"cache_write":0.125,"in":0.10,"out":0.50,"cached":0.01},"gpt-6-sol":{"cached":0.2,"in":2,"out":10,"cache_write":2.5}}""";

        PriceVersionRates.Parse(shuffled).ToJson().ShouldBe(PriceVersionRates.Parse(reordered).ToJson());
    }

    [Fact]
    public void Qa_PriceVersionRates_ToJson_NormalisesDecimalScale()
    {
        var padded = PriceVersionRates.Parse("""{"gpt-6-luna":{"in":0.10,"cached":0.010,"cache_write":0.1250,"out":0.500},"web_search_call":0.0100}""");
        var plain = PriceVersionRates.Parse("""{"gpt-6-luna":{"in":0.1,"cached":0.01,"cache_write":0.125,"out":0.5},"web_search_call":0.01}""");

        padded.ToJson().ShouldBe(plain.ToJson(), "0.10 and 0.1 are one rate, so they hash to one config");
        plain.ToJson().ShouldContain("\"in\":0.1,");
    }

    [Fact]
    public void Qa_PriceVersionRates_SurchargeThreshold_IsPartOfTheDocumentAndRoundTrips()
    {
        var rates = PriceVersionRates.Parse("""{"surcharge_threshold_tokens":1000,"gpt-6-luna":{"in":1,"cached":1,"cache_write":1,"out":1}}""");

        rates.SurchargeThresholdTokens.ShouldBe(1000);
        rates.Text.ShouldContainKey("gpt-6-luna");
        rates.Text.ShouldNotContainKey("surcharge_threshold_tokens");
        PriceVersionRates.Parse(rates.ToJson()).SurchargeThresholdTokens.ShouldBe(1000);

        var other = rates with { SurchargeThresholdTokens = 2000 };
        other.ToJson().ShouldNotBe(rates.ToJson(), "a different threshold is a different config hash");
    }

    [Fact]
    public void Qa_PriceVersionRates_AbsentThreshold_DefaultsToO3() =>
        PriceVersionRates.Parse("""{"gpt-6-luna":{"in":1,"cached":1,"cache_write":1,"out":1}}""")
            .SurchargeThresholdTokens.ShouldBe(TextCostCalculator.DefaultSurchargeThresholdTokens);

    [Fact]
    public void Qa_PriceVersionRates_MissingField_ThrowsJsonException() =>
        Should.Throw<System.Text.Json.JsonException>(() => PriceVersionRates.Parse("""{"gpt-6-luna":{"in":1,"cached":1,"out":1}}"""));

    [Fact]
    public void Qa_RatesFor_BlankModel_Throws() =>
        Should.Throw<ArgumentException>(() => new PriceVersionRates(
            new Dictionary<string, PriceRates>(),
            new Dictionary<string, ImagePriceRate>(),
            null).RatesFor(" "));
}
