using NightTales.Application.Features.VideoReview;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.VideoReview;

/// <summary>
/// The <c>Assets:RetentionDays</c> window of 09.7.1-T1 (WT56): the 30-day default the approve path re-stamps,
/// and the <c>1..365</c> band each host fails its start-up outside of.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AssetRetentionOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreAThirtyDayWindowInTheAssetsSection()
    {
        var options = new AssetRetentionOptions();

        AssetRetentionOptions.SectionName.ShouldBe("Assets");
        options.RetentionDays.ShouldBe(30);
        options.Retention.ShouldBe(TimeSpan.FromDays(30));
        AssetRetentionOptionsValidator.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(AssetRetentionOptionsValidator.MaxRetentionDays)]
    public void InsideTheBand_IsAccepted(int days)
    {
        AssetRetentionOptionsValidator.Validate(new AssetRetentionOptions { RetentionDays = days }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AssetRetentionOptionsValidator.MaxRetentionDays + 1)]
    public void OutsideTheBand_IsRefusedWithTheKeyNamed(int days)
    {
        var problems = AssetRetentionOptionsValidator.Validate(new AssetRetentionOptions { RetentionDays = days });

        problems.ShouldHaveSingleItem();
        problems[0].ShouldContain(nameof(AssetRetentionOptions.RetentionDays), Case.Sensitive);
    }

    [Fact]
    public void ConfiguredDays_AreWhatTheWindowCarries()
    {
        var options = new AssetRetentionOptions { RetentionDays = 7 };

        options.Retention.ShouldBe(TimeSpan.FromDays(7));
    }
}
