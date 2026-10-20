using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The orchestrator cases of 02.8.3-T1 §Tests: one tick of a run pays at most once, a tick that loses the
/// <c>xmin</c> race writes nothing and says so, and a series never runs two producing slots at once.
/// </summary>
/// <remarks>
/// Every case drives the real <c>AutopilotRunOrchestrator</c> and the real step handlers; only the idea planner and
/// the story gate are scripted. The two racing ticks are parked inside the step's own seam (the planner call and the
/// gate call) with a two-party barrier, so the interleaving is forced, not hoped for.
/// </remarks>
[Collection("Process-global host state")]
public sealed class AutopilotOrchestratorTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    /// <summary>How long a parked tick may take before the test fails instead of hanging.</summary>
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Orchestrator_DuplicateTick_DoesNotPayTwice()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: TestContext.Current.CancellationToken);

        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: Start.AddHours(36),
            startAt: Start.AddHours(-12),
            authorizationId: world.AuthorizationId,
            ct: TestContext.Current.CancellationToken);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Ideas.BeforeAnswer = async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var first = Task.Run(() => harness.AdvanceAsync(run.Id, owner, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(BarrierTimeout, TestContext.Current.CancellationToken);

        // The first tick holds a committed `generation_runs` row while it pays: the second tick adopts it instead
        // of paying again.
        var second = await harness.AdvanceAsync(run.Id, owner, TestContext.Current.CancellationToken);
        second.IsError.ShouldBeFalse();
        second.Value.Kind.ShouldBe(StepResultKind.Idle);
        second.Value.Code.ShouldBe("idea_in_flight");

        release.TrySetResult();
        var firstResult = await first.WaitAsync(BarrierTimeout, TestContext.Current.CancellationToken);
        firstResult.IsError.ShouldBeFalse();
        firstResult.Value.Kind.ShouldBe(StepResultKind.Advanced);

        harness.Ideas.CallCount.ShouldBe(1);
        (await harness.CountGenerationRunsAsync(world.SeriesId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await harness.GenerationRunKeysAsync(world.SeriesId, TestContext.Current.CancellationToken)).Distinct().Count().ShouldBe(1);

        var stored = await harness.RunAsync(owner, run.Id, TestContext.Current.CancellationToken);
        stored.State.ShouldBe(AutopilotRunState.IdeaPicked);
        stored.Step.ShouldBe(AutopilotRunStep.Idea);
        stored.IdeaId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Orchestrator_ConcurrentTicks_SaveOnce_LoserIsIdle()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: TestContext.Current.CancellationToken);

        var episodeId = await harness.CreateEpisodeAsync(owner, world.SeriesId, ct: TestContext.Current.CancellationToken);
        var run = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: Start.AddHours(36),
            startAt: Start.AddHours(-12),
            authorizationId: world.AuthorizationId,
            state: AutopilotRunState.StoryGate,
            step: AutopilotRunStep.StoryGate,
            episodeId: episodeId,
            attempt: 1,
            ct: TestContext.Current.CancellationToken);

        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Gate.BeforeStoryAnswer = async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.ConfigureAwait(false);
        };

        var results = await Task.WhenAll(
                Task.Run(() => harness.AdvanceAsync(run.Id, owner, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
                Task.Run(() => harness.AdvanceAsync(run.Id, owner, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))
            .WaitAsync(BarrierTimeout, TestContext.Current.CancellationToken);

        results.ShouldAllBe(result => !result.IsError);
        results.Count(result => result.Value.Kind == StepResultKind.Advanced).ShouldBe(1);
        results.Count(result => result.Value.Kind == StepResultKind.Idle
            && result.Value.Code == AutopilotRunOrchestrator.ConcurrencyLostCode).ShouldBe(1);

        harness.Gate.StoryCallCount.ShouldBe(2);

        var stored = await harness.RunAsync(owner, run.Id, TestContext.Current.CancellationToken);
        stored.State.ShouldBe(AutopilotRunState.Rendering);
        stored.Step.ShouldBe(AutopilotRunStep.StoryGate);
    }

    [Fact]
    public async Task Orchestrator_ConcurrentProducingRun_LogsViolationAndSkips()
    {
        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            ct: TestContext.Current.CancellationToken);
        var owner = await harness.CreateOwnerAsync(TestContext.Current.CancellationToken);
        var world = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: TestContext.Current.CancellationToken);

        // One producing run of the series is already ahead of this slot.
        await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: Start.AddHours(36),
            startAt: Start.AddHours(-20),
            authorizationId: world.AuthorizationId,
            ct: TestContext.Current.CancellationToken);

        var behind = await harness.CreateRunAsync(
            owner,
            world.SeriesId,
            slotAt: Start.AddHours(60),
            startAt: Start.AddHours(-12),
            authorizationId: world.AuthorizationId,
            ct: TestContext.Current.CancellationToken);

        using var violations = AutopilotMetricProbe.Start(AutopilotRunMetrics.ConcurrencyViolationTotalName);

        var result = await harness.AdvanceAsync(behind.Id, owner, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Value.Kind.ShouldBe(StepResultKind.Idle);
        result.Value.Code.ShouldBe(AutopilotRunOrchestrator.ProducingRunAheadCode);

        violations.Count().ShouldBe(1);
        harness.Ideas.CallCount.ShouldBe(0);
        harness.Logs.AllText().ShouldContain("concurrency_violation");

        var stored = await harness.RunAsync(owner, behind.Id, TestContext.Current.CancellationToken);
        stored.State.ShouldBe(AutopilotRunState.Planned);
    }
}
