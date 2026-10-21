using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The tick's due scan against a real database (02.8.3-T1; review findings PF-1, SEC-2, PF-3): a queue of idle-waiting
/// runs larger than the per-tick budget never keeps a run that needs the tick out of it, the scan rotates through the
/// waiting rows instead of re-reading its head, and a saturated pass says so in the log.
/// </summary>
/// <remarks>
/// Every case drives the real orchestrator and the real step handlers over <see cref="AutopilotTickHarness"/>; only the
/// idea planner and the story gate are scripted. The idle-waiting rows are <c>awaiting_authorization</c> runs of a
/// series with no accepted authorization: their step answers idle on every tick and never advances on its own, which is
/// exactly the shape that starved the scan.
/// </remarks>
[Collection("Process-global host state")]
public sealed class AutopilotDueScanTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    /// <summary>The tick's interval: the plan job runs every fifteen minutes.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    /// <summary>How many idle-waiting runs the cases seed — comfortably more than their budget.</summary>
    private const int IdleRuns = 12;

    /// <summary>
    /// Twelve idle-waiting runs, one planned run and a budget of five: the run that needs the tick is still advanced in
    /// that one tick, and the idle rows it did not reach stay where they were (PF-1, SEC-2).
    /// </summary>
    [Fact]
    public async Task DueScan_WithMoreIdleWaitingRunsThanTheBudget_StillAdvancesTheRunThatNeedsTheTick()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        await ResetDueQueueAsync(harness, ct);
        var owner = await harness.CreateOwnerAsync(ct);
        var idle = await harness.CreateSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(),
            authorize: false,
            ct: ct);
        var live = await harness.CreateAuthorizedSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(daysOfWeek: [DayOfWeek.Thursday]),
            ct: ct);

        var idleRunIds = await SeedAwaitingAuthorizationRunsAsync(harness, owner, idle.SeriesId, ct);

        // The run that needs the tick is created last, so a scan without an order takes the idle rows first.
        var needed = await harness.CreateRunAsync(
            owner,
            live.SeriesId,
            slotAt: Start.AddHours(36),
            startAt: Start.AddHours(-12),
            authorizationId: live.AuthorizationId,
            ct: ct);

        harness.Clock.Advance(Tick);

        using var scope = harness.Host.CreateScope();
        var advanced = await scope.ServiceProvider
            .GetRequiredService<IAutopilotRunOrchestrator>()
            .AdvanceDueAsync(harness.Clock.UtcNow, 5, ct);

        advanced.ShouldBeGreaterThanOrEqualTo(1);

        var neededAfter = await harness.RunAsync(owner, needed.Id, ct);
        neededAfter.State.ShouldNotBe(
            AutopilotRunState.Planned,
            "a queue of idle-waiting runs must not keep a run that needs the tick out of the budget");

        var idleAfter = await harness.RunsAsync(owner, idle.SeriesId, ct);
        idleAfter.Count.ShouldBe(IdleRuns);
        idleAfter.ShouldAllBe(
            run => run.State == AutopilotRunState.AwaitingAuthorization,
            "the waiting rows are untouched by a scan that only re-reads them");
        idleRunIds.Count.ShouldBe(IdleRuns);
    }

    /// <summary>
    /// The scan rotates: the second tick re-reads the rows the first one did not reach, because the served rows are
    /// re-stamped and the pass orders by that stamp (PF-1's rotation, without a new column).
    /// </summary>
    [Fact]
    public async Task DueScan_ReStampsWhatItServed_SoTheNextTickReadsOtherRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        await ResetDueQueueAsync(harness, ct);
        var owner = await harness.CreateOwnerAsync(ct);
        var idle = await harness.CreateSeriesAsync(
            owner,
            AutopilotTickHarness.Settings(),
            authorize: false,
            ct: ct);

        await SeedAwaitingAuthorizationRunsAsync(harness, owner, idle.SeriesId, ct);

        harness.Clock.Advance(Tick);
        var first = await TickAsync(harness, owner, 4, ct);

        harness.Clock.Advance(Tick);
        var second = await TickAsync(harness, owner, 4, ct);

        first.Count.ShouldBe(4);
        second.Count.ShouldBe(4);
        second.Intersect(first).ShouldBeEmpty("the rows served by the first tick are at the back of the queue");

        var runs = await harness.RunsAsync(owner, idle.SeriesId, ct);
        var observed = string.Join(", ", runs.Select(run => run.UpdatedAt.ToString("O")).Distinct());

        runs.Count(run => run.UpdatedAt == harness.Clock.UtcNow).ShouldBe(4, $"stamps seen: {observed}");
        runs.Count(run => run.UpdatedAt == Start + Tick).ShouldBe(4, $"stamps seen: {observed}");
        runs.Count(run => run.UpdatedAt == Start).ShouldBe(IdleRuns - 8, $"stamps seen: {observed}");
    }

    /// <summary>
    /// A pass whose actionable rows alone fill the budget is logged as truncated, so a saturated tick is
    /// distinguishable from a tick that simply had nothing to do (PF-3).
    /// </summary>
    [Fact]
    public async Task DueScan_ActionableRowsFillTheBudget_LogsTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AutopilotTickHarness.StartAsync(Database, new FakeClock(Start), ct: ct);

        await ResetDueQueueAsync(harness, ct);
        var owner = await harness.CreateOwnerAsync(ct);

        foreach (var day in new[] { DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday })
        {
            var series = await harness.CreateAuthorizedSeriesAsync(
                owner,
                AutopilotTickHarness.Settings(daysOfWeek: [day]),
                ct: ct);

            await harness.CreateRunAsync(
                owner,
                series.SeriesId,
                slotAt: Start.AddHours(36),
                startAt: Start.AddHours(-12),
                authorizationId: series.AuthorizationId,
                ct: ct);
        }

        harness.Clock.Advance(Tick);

        using var scope = harness.Host.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IAutopilotRunOrchestrator>()
            .AdvanceDueAsync(harness.Clock.UtcNow, 1, ct);

        harness.Logs.AllText().ShouldContain("autopilot_tick_truncated");
    }

    /// <summary>
    /// Empties the due queue first: the scan is deliberately cross-user, so a row an earlier case of this collection
    /// left behind would take the budget these cases are about (the collection serialises them, never runs them
    /// together).
    /// </summary>
    private static Task ResetDueQueueAsync(AutopilotTickHarness harness, CancellationToken ct) =>
        harness.Database.ExecuteAsync("DELETE FROM autopilot_runs", ct);

    /// <summary>One due scan, returning the runs it re-stamped at the tick's own instant.</summary>
    private static async Task<IReadOnlyList<Guid>> TickAsync(
        AutopilotTickHarness harness,
        Guid owner,
        int max,
        CancellationToken ct)
    {
        var now = harness.Clock.UtcNow;

        using var scope = harness.Host.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IAutopilotRunOrchestrator>()
            .AdvanceDueAsync(now, max, ct);

        var runs = await harness.RunsAsync(owner, seriesId: null, ct);

        return [.. runs.Where(run => run.UpdatedAt == now).Select(run => run.Id)];
    }

    /// <summary>
    /// Seeds <see cref="IdleRuns"/> <c>awaiting_authorization</c> rows for one unauthorized series; their slots are
    /// distinct because the planner's own unique key is <c>(series_id, slot_at)</c>.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SeedAwaitingAuthorizationRunsAsync(
        AutopilotTickHarness harness,
        Guid owner,
        Guid seriesId,
        CancellationToken ct)
    {
        var ids = new List<Guid>(IdleRuns);

        for (var index = 0; index < IdleRuns; index++)
        {
            var run = await harness.CreateRunAsync(
                owner,
                seriesId,
                slotAt: Start.AddDays(1).AddMinutes(index),
                startAt: Start.AddHours(-1),
                state: AutopilotRunState.AwaitingAuthorization,
                step: AutopilotRunStep.Authorization,
                ct: ct);

            ids.Add(run.Id);
        }

        return ids;
    }
}
