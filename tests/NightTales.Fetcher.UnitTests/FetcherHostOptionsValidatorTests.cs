using NightTales.Fetcher.Core.Fetching;
using Shouldly;
using Xunit;

namespace NightTales.Fetcher.UnitTests;

/// <summary>02.5.2-T3 review fixes CR-2 and CR-3: the start-up rules that only the fetcher host enforces.</summary>
public sealed class FetcherHostOptionsValidatorTests
{
    private static readonly FetcherHostOptionsValidator Validator = new();

    [Fact]
    public void Defaults_Pass() =>
        Validator.Validate(null, new FetcherOptions()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void MaxLeasesPlatformWide_WithinServerCap_Passes(int value) =>
        Validator.Validate(null, new FetcherOptions { MaxLeasesPlatformWide = value }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(64)]
    public void MaxLeasesPlatformWide_OutsideServerCap_FailsWithNamedError(int value)
    {
        var result = Validator.Validate(null, new FetcherOptions { MaxLeasesPlatformWide = value });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Fetcher:MaxLeasesPlatformWide");
    }

    [Fact]
    public void WatchdogBound_JustBelowCeiling_Passes() =>
        Validator.Validate(null, new FetcherOptions { TotalTimeoutSeconds = 200, ExtractTimeoutSeconds = 99, MaxLeaseTotalSeconds = 300 })
            .Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(200, 100)]
    [InlineData(250, 100)]
    public void WatchdogBound_AtOrAboveCeiling_FailsWithNamedError(int total, int extract)
    {
        var result = Validator.Validate(null, new FetcherOptions { TotalTimeoutSeconds = total, ExtractTimeoutSeconds = extract, MaxLeaseTotalSeconds = 300 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxLeaseTotalSeconds");
    }

    [Fact]
    public void WatchdogBound_IntOverflowSum_StillFails()
    {
        var result = Validator.Validate(null, new FetcherOptions { TotalTimeoutSeconds = int.MaxValue, ExtractTimeoutSeconds = int.MaxValue });

        result.Failed.ShouldBeTrue();
    }
}
