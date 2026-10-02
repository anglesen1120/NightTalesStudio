using System.Diagnostics;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The poll cycle's two review fixes: a stalled MPT status call must not hold up another task's update
/// (PERF-1), and a row persisted at <c>checking</c>/100 must be handed on again after an adapter restart
/// (CODE-1). Both are behaviours of <c>PollOnceAsync</c> as a whole, not of one task's mapping.
/// </summary>
public sealed class PollCycleTests
{
    private const string SlowMptTaskId = "9f5d2b71-3c4a-4e6d-8a1b-111111111111";
    private const string FastMptTaskId = "9f5d2b71-3c4a-4e6d-8a1b-222222222222";
    private const string RestartMptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    [Fact]
    public async Task StalledStatusCall_DoesNotStopAnotherTaskFromReachingTerminalState()
    {
        MptTaskPoller.MaxConcurrentPolls.ShouldBeGreaterThan(1, "PERF-1 needs more than one poll at a time");

        await using var harness = await AdapterHarness.StartAsync();

        // The stalled task sorts first (ListNonTerminalAsync orders by submitted_at), so a serial cycle would
        // wait for its 2 s status call before it even asks the second task.
        var slow = AdapterHarness.NewRecord(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            backendTaskId: SlowMptTaskId) with
        {
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        var fast = AdapterHarness.NewRecord(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            backendTaskId: FastMptTaskId) with
        {
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
        };
        await harness.SeedAsync(slow);
        await harness.SeedAsync(fast);

        harness.Mpt.Script(SlowMptTaskId, 4, 40);
        harness.Mpt.Script(FastMptTaskId, -1, 20, failedStage: "audio");
        harness.Mpt.DelayTask(SlowMptTaskId, TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var cycle = harness.PollOnceAsync();

        var terminal = false;
        while (!terminal && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            terminal = (await harness.Store.TryGetAsync(fast.TaskId, CancellationToken.None))?.IsTerminal == true;
            if (!terminal)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        terminal.ShouldBeTrue("the second task must reach a terminal state in the same cycle");
        stopwatch.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(2),
            "a stalled status call must not delay another task's poll behind it");

        await cycle;

        (await harness.RowAsync(fast.TaskId)).State.ShouldBe(RenderStates.Failed);
        (await harness.RowAsync(slow.TaskId)).State.ShouldBe(RenderStates.Running, "the stalled task still updated");
        harness.Mpt.TaskRequests
            .Select(request => request.Path)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(2, "both tasks were asked about in the same cycle");
    }

    [Fact]
    public async Task CheckingRowPersistedBeforeRestart_IsRepublishedOnceByTheFreshPoller()
    {
        await using var first = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: RestartMptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Checking,
            percent: 100);
        await first.SeedAsync(record);
        var dataDir = first.Config.DataDir;

        // The restart: a fresh store, client, poller and feed over the same DataDir. The previous process died
        // after the checking write (or while T3's post-processor ran), so its in-memory feed is gone.
        await using var restarted = await AdapterHarness.StartAsync(options => options.DataDir = dataDir);
        restarted.Mpt.Script(RestartMptTaskId, 1, 100);

        await restarted.PollOnceAsync();

        // T2 owns the hand-off, not the terminal write: T3's post-processor consumes this feed and sets
        // succeeded|failed, so the recovery's observable is that the persisted row is delivered again.
        var published = await restarted.ReadPublishedAsync(1, TimeSpan.FromSeconds(2));
        published.Count.ShouldBe(1, "the persisted checking row is handed to the post-processor again");
        published[0].TaskId.ShouldBe(record.TaskId);
        published[0].Stage.ShouldBe(RenderStages.Checking);
        restarted.Mpt.TaskRequests.ShouldBeEmpty("MPT is done; the post-processor owns the row");

        // Exactly once per process lifetime: the next cycle neither re-polls nor re-publishes.
        await restarted.PollOnceAsync();
        (await restarted.ReadPublishedAsync(1, TimeSpan.FromMilliseconds(300))).ShouldBeEmpty();
        restarted.Mpt.TaskRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NormalPolling_DoesNotRepublishACompletionTwice()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: RestartMptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);
        harness.Mpt.Script(RestartMptTaskId, 1, 100);

        // Cycle one completes the task; the recovery pass of that same cycle must not publish it a second time.
        await harness.PollOnceAsync();
        var published = await harness.ReadPublishedAsync(2, TimeSpan.FromMilliseconds(500));
        published.Count.ShouldBe(1, "one completion is one hand-off");

        await harness.PollOnceAsync();
        (await harness.ReadPublishedAsync(1, TimeSpan.FromMilliseconds(300))).ShouldBeEmpty();
    }
}
