using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Usage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// 02.7.2-T2: the start-up bounds of the <c>SourceFinder</c> section — the estimator turns them into the money a
/// run reserves, so they are validated where they are bound — and the worst-case bound itself: the finder call at
/// the byte ceiling plus <c>MaxSearchCalls</c> times the version's flat web-search fee.
/// </summary>
public sealed class FindSourcesCostOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new FindSourcesCostOptions();

        options.Model.ShouldBe(FindSourcesCostEstimator.DefaultModel);
        options.MaxSearchCalls.ShouldBe(FindSourcesCostOptions.DefaultMaxSearchCalls);
        options.MaxInputBytes.ShouldBe(FindSourcesCostOptions.DefaultMaxInputBytes);
        FindSourcesCostOptionsValidator.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FindSourcesCostOptions.MaxMaxSearchCalls + 1)]
    [InlineData(int.MaxValue)]
    public void MaxSearchCalls_OutsideTheBound_FailsStartupValidation(int value)
    {
        var options = new FindSourcesCostOptions { MaxSearchCalls = value };

        FindSourcesCostOptionsValidator.Validate(options)
            .ShouldHaveSingleItem()
            .ShouldContain(nameof(FindSourcesCostOptions.MaxSearchCalls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FindSourcesCostOptions.MaxMaxInputBytes + 1)]
    [InlineData(int.MaxValue)]
    public void MaxInputBytes_OutsideTheBound_FailsStartupValidation(int value)
    {
        var options = new FindSourcesCostOptions { MaxInputBytes = value };

        FindSourcesCostOptionsValidator.Validate(options)
            .ShouldHaveSingleItem()
            .ShouldContain(nameof(FindSourcesCostOptions.MaxInputBytes));
    }

    [Fact]
    public void ValuesAtTheBounds_Pass()
    {
        var options = new FindSourcesCostOptions
        {
            MaxSearchCalls = FindSourcesCostOptions.MaxMaxSearchCalls,
            MaxInputBytes = FindSourcesCostOptions.MaxMaxInputBytes,
        };

        FindSourcesCostOptionsValidator.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Model_Missing_FailsStartupValidation(string model)
    {
        var options = new FindSourcesCostOptions { Model = model };

        FindSourcesCostOptionsValidator.Validate(options).ShouldHaveSingleItem().ShouldContain("Model");
    }
}

/// <summary>
/// <c>IFindSourcesCostEstimator</c> (02 README D1/D14/T56, 02.7 plan §7): the finder call priced at the version's
/// rates with the input charged as a cache write, plus the per-call web-search fee, and fail-closed when the
/// version carries no fee at all.
/// </summary>
public sealed class FindSourcesCostEstimatorTests
{
    private static readonly PriceRates Luna = new(0.10m, 0.01m, 0.125m, 0.50m);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorstCase_IsTheTokenBoundPlusCallsTimesTheWebSearchRate()
    {
        var bound = (await Estimator(options: new FindSourcesCostOptions(), webSearchCall: 0.01m).WorstCaseAsync(Ct)).Value;

        // (6000 bytes in + 6000 bytes charged as a cache write) at luna's rates + 2000 output, plus 3 x 0.01.
        bound.MaxInputTokens.ShouldBe(FindSourcesCostOptions.DefaultMaxInputBytes);
        bound.MaxOutputTokens.ShouldBe(FindSourcesCostEstimator.FinderMaxOutputTokens);
        bound.WorstCaseUsd.ShouldBe(0.03235m);
    }

    [Fact]
    public async Task WorstCase_GrowsByTheRateForEveryExtraSearchCall()
    {
        var one = (await Estimator(new FindSourcesCostOptions { MaxSearchCalls = 1 }, 0.01m).WorstCaseAsync(Ct)).Value;
        var three = (await Estimator(new FindSourcesCostOptions { MaxSearchCalls = 3 }, 0.01m).WorstCaseAsync(Ct)).Value;

        (three.WorstCaseUsd - one.WorstCaseUsd).ShouldBe(0.02m);
    }

    [Fact]
    public async Task WorstCase_WithoutAWebSearchRate_FailsClosed()
    {
        // Reserving the token cost alone would under-book every search call: the estimator refuses instead.
        await Should.ThrowAsync<InvalidOperationException>(
            () => Estimator(new FindSourcesCostOptions(), webSearchCall: null).WorstCaseAsync(Ct));
    }

    [Fact]
    public async Task WorstCase_CannotBecomeNegative_WhenAHostBypassedTheStartupBounds()
    {
        var bound = (await Estimator(new FindSourcesCostOptions { MaxSearchCalls = -1, MaxInputBytes = -1 }, 0.01m)
            .WorstCaseAsync(Ct)).Value;

        bound.WorstCaseUsd.ShouldBe(0.001m, "only the 2000 output tokens are left to price");
        bound.MaxInputTokens.ShouldBe(0);
    }

    private static FindSourcesCostEstimator Estimator(FindSourcesCostOptions options, decimal? webSearchCall) =>
        new(new FakeCatalog(webSearchCall), options, new FakeClock());

    /// <summary>A catalog with one active version that carries the finder model's rates and the given fee.</summary>
    private sealed class FakeCatalog(decimal? webSearchCall) : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(Build());

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) => Task.FromResult(Build());

        private PriceVersion Build()
        {
            var rates = new PriceVersionRates(
                new Dictionary<string, PriceRates> { [FindSourcesCostEstimator.DefaultModel] = Luna },
                new Dictionary<string, ImagePriceRate>(),
                webSearchCall,
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
