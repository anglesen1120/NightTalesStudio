using NightTales.Application.Abstractions.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories;

/// <summary>The <c>Budget</c> section defaults (02 README D1, 02.2.2-T2).</summary>
public sealed class BudgetOptionsTests
{
    [Fact]
    public void BudgetOptions_Defaults_AreThreeAndForty()
    {
        var options = new BudgetOptions();

        options.Enabled.ShouldBeTrue();
        options.MaxPlatformCostUsdPerDay.ShouldBe(3m);
        options.MaxPlatformCostUsdPerMonth.ShouldBe(40m);
        BudgetOptions.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(3, -1)]
    public void BudgetOptions_NonPositiveCap_FailsValidation(int day, int month)
    {
        var options = new BudgetOptions { MaxPlatformCostUsdPerDay = day, MaxPlatformCostUsdPerMonth = month };

        BudgetOptions.Validate(options).ShouldNotBeEmpty();
    }
}
