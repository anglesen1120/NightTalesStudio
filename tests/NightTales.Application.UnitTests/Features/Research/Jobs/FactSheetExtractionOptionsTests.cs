using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.Jobs;

/// <summary>
/// 02.5.3-T2 (N5): <c>FactSheetExtractionOptions.Validate</c> — the fact-sheet job's own copy of the
/// <c>Facts</c> budget. It must state the same <c>MaxInputTokens</c> bound as <c>FactsOptionsValidator</c>
/// (1..<see cref="FactInputLimits.MaxMaxInputTokens"/>), because the cost estimator books four times that
/// ceiling as input bytes and the bound's token count is an <see cref="int"/>.
/// </summary>
public sealed class FactSheetExtractionOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var options = new FactSheetExtractionOptions();

        options.MaxInputTokens.ShouldBe(FactInputLimits.DefaultMaxInputTokens);
        FactSheetExtractionOptions.Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(FactInputLimits.DefaultMaxInputTokens)]
    [InlineData(FactInputLimits.MaxMaxInputTokens)]
    public void Validate_AcceptsMaxInputTokensInsideTheBound(int minutes)
    {
        var problems = FactSheetExtractionOptions.Validate(new FactSheetExtractionOptions { MaxInputTokens = minutes });

        problems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FactInputLimits.MaxMaxInputTokens + 1)]
    public void Validate_RejectsMaxInputTokensOutsideTheBound(int value)
    {
        var problems = FactSheetExtractionOptions.Validate(new FactSheetExtractionOptions { MaxInputTokens = value });

        problems.ShouldHaveSingleItem().ShouldContain($"'{FactSheetExtractionOptions.SectionName}:MaxInputTokens'");
        problems[0].ShouldContain($"1..{FactInputLimits.MaxMaxInputTokens}");
    }

    [Fact]
    public void Validate_RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => FactSheetExtractionOptions.Validate(null!));
}
