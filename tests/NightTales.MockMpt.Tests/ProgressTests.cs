using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The progress mapping of 05 D4 / 11.1.2-T1's Design: the mock walks <c>queued → running → checking →
/// succeeded</c>, <c>percent</c> never decreases, and 100 appears only at the end.
/// </summary>
public sealed class ProgressTests
{
    [Fact]
    public async Task Progress_NonDecreasing_ReachesHundredOnlyTerminal()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });

        var started = DateTimeOffset.UtcNow;
        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        var statuses = await host.PollUntilTerminalAsync(taskId, TimeSpan.FromSeconds(6));
        var elapsed = DateTimeOffset.UtcNow - started;

        statuses.Count.ShouldBeGreaterThan(1, "the walk must be observable, not a single terminal answer");
        statuses.Any(status => !RenderStates.IsTerminal(status.State))
            .ShouldBeTrue("at least one poll must catch the task before it finishes");

        for (var index = 1; index < statuses.Count; index++)
        {
            statuses[index].Percent.ShouldBeGreaterThanOrEqualTo(
                statuses[index - 1].Percent,
                $"percent decreased at status {index}: {statuses[index - 1].Percent} → {statuses[index].Percent}");
        }

        foreach (var status in statuses.Where(status => status.Percent == 100))
        {
            RenderStates.IsTerminal(status.State).ShouldBeTrue(
                $"percent reached 100 while state was '{status.State}' (stage '{status.Stage}')");
        }

        var last = statuses[^1];
        last.State.ShouldBe(RenderStates.Succeeded);
        last.Stage.ShouldBe(RenderStages.Done);
        last.Percent.ShouldBe(100);
        last.FinishedAt.ShouldNotBeNull();
        last.Output.ShouldNotBeNull();
        last.Check.ShouldNotBeNull();
        last.Images.ShouldNotBeNull();
        last.Mpt.ShouldNotBeNull();

        // The acceptance budget: terminal within MOCK_RENDER_SECONDS + 2 s.
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }
    [Theory]
    [InlineData(0, "queued", "queued", 0)]
    [InlineData(30, "running", "voice", 5)]
    [InlineData(295, "running", "voice", 29)]
    [InlineData(305, "running", "subtitles", 30)]
    [InlineData(395, "running", "subtitles", 39)]
    [InlineData(405, "running", "images", 40)]
    [InlineData(495, "running", "images", 49)]
    [InlineData(505, "running", "assembling", 50)]
    [InlineData(990, "running", "assembling", 99)]
    [InlineData(1000, "running", "assembling", 99)]
    public void ProgressClock_BoundaryPercents_MapToThePinnedStages(
        int elapsedMs, string state, string stage, int percent)
    {
        // The 05 D4 mapping across its stage boundaries: voice 5–29, subtitles 30–39, images 40–49,
        // assembling 50–99; the cap keeps 100 for the terminal tick even at 100 % elapsed.
        var (actualState, actualStage, actualPercent) = NightTales.MockMpt.Backend.ProgressClock.At(
            TimeSpan.FromMilliseconds(elapsedMs), TimeSpan.FromSeconds(1));

        (actualState, actualStage, actualPercent).ShouldBe((state, stage, percent));
    }
}
