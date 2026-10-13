using NightTales.Application.Features.Research.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.Jobs;

/// <summary>
/// 02.5.3-T2 (CODE-3): the <c>FactSheet:Job</c> bounds. One run pays for the free moderation call (15 s) and up
/// to two extraction attempts at <c>OpenAi:RequestTimeout:Facts</c> (60 s each), so a deadline below that floor
/// would cancel a request that was already sent and billed rather than letting the run finish.
/// </summary>
public sealed class FactSheetJobOptionsTests
{
    /// <summary>The moderation timeout plus the two adapter timeouts one run can need (15 + 60 + 60).</summary>
    private const int AdapterFloorSeconds = 135;

    /// <summary>The story sweeper's window: what reclaims a fact run a shutdown left <c>running</c>.</summary>
    private const int StorySweeperWindowSeconds = 360;

    [Fact]
    public void Default_LeavesHeadroomOverTheFloor_AndStaysUnderTheStorySweeperWindow()
    {
        var options = new FactSheetJobOptions();

        options.TimeoutSeconds.ShouldBe(240);
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(240));
        options.TimeoutSeconds.ShouldBeGreaterThan(AdapterFloorSeconds);
        options.TimeoutSeconds.ShouldBeLessThanOrEqualTo(StorySweeperWindowSeconds);
        FactSheetJobOptions.Validate(options).ShouldBeEmpty();
    }

    [Fact]
    public void MinTimeoutSeconds_CoversTheModerationCallAndBothAdapterTimeouts() =>
        FactSheetJobOptions.MinTimeoutSeconds.ShouldBeGreaterThanOrEqualTo(AdapterFloorSeconds);

    [Fact]
    public void MaxTimeoutSeconds_IsTheStorySweeperWindow() =>
        FactSheetJobOptions.MaxTimeoutSeconds.ShouldBe(
            StorySweeperWindowSeconds,
            "a longer fact deadline would let the story sweeper fail a healthy run still inside its own budget");

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(149)]
    [InlineData(361)]
    public void Validate_RejectsAValueOutsideTheBounds(int seconds)
    {
        var problems = FactSheetJobOptions.Validate(new FactSheetJobOptions { TimeoutSeconds = seconds });

        problems.ShouldHaveSingleItem().ShouldContain($"'{FactSheetJobOptions.SectionName}:TimeoutSeconds'");
        problems[0].ShouldContain($"{FactSheetJobOptions.MinTimeoutSeconds} and {FactSheetJobOptions.MaxTimeoutSeconds}");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(240)]
    [InlineData(360)]
    public void Validate_AcceptsEveryValueInsideTheBounds(int seconds) =>
        FactSheetJobOptions.Validate(new FactSheetJobOptions { TimeoutSeconds = seconds }).ShouldBeEmpty();

    [Fact]
    public void Validate_RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => FactSheetJobOptions.Validate(null!));
}
