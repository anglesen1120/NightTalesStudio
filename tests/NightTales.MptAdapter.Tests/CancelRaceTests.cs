using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// CODE-2 from the T3 review: the poller reads a row, awaits MPT, and writes what MPT said. A
/// <c>POST …/cancel</c> that lands in that window must win — the row ends <c>cancelled</c> with its directory
/// removed, never <c>failed</c> with <c>cancel_requested</c> set (which <c>ListNonTerminalAsync</c> would
/// exclude forever).
/// </summary>
public sealed class CancelRaceTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    [Fact]
    public async Task CancelLandingDuringTheStatusCall_BecomesCancelledAndCleansUp()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);
        harness.WriteTaskFiles(record.TaskId, MptTaskId);

        // MPT is about to answer a terminal failure, but its answer takes 2 s.
        harness.Mpt.Script(MptTaskId, -1, 40, failedStage: "audio");
        harness.Mpt.DelayTask(MptTaskId, TimeSpan.FromSeconds(2));

        var cycle = harness.PollOnceAsync();
        (await harness.Mpt.WaitForTaskRequestAsync($"/api/v1/tasks/{MptTaskId}", TimeSpan.FromSeconds(3)))
            .ShouldNotBeNull("the status call must be in flight before the cancel lands");

        // The cancel persists while the poller's snapshot still says cancel_requested = false.
        (await harness.Store.UpdateAsync(record with { CancelRequested = true }, CancellationToken.None))
            .ShouldBeTrue();

        await cycle;

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Cancelled, "a persisted cancel wins over the failure MPT reported");
        row.Stage.ShouldBe(RenderStages.Done);
        row.Percent.ShouldBe(100);
        row.Outcome.ShouldBeNull("05 D4: a cancelled task carries no outcome");
        row.CancelRequested.ShouldBeTrue();
        harness.TaskFilesExist(record.TaskId, MptTaskId).ShouldBeFalse(
            "the cancel's cleanup runs at once (05 D15 P4)");
        (await harness.ReadPublishedAsync(1, TimeSpan.FromMilliseconds(200)))
            .ShouldBeEmpty("a cancelled task must never reach the post-processor");
        harness.Logs.Contains("is cancelled and its task dir was removed").ShouldBeTrue();
    }

    [Fact]
    public async Task CancelLandingDuringTheStatusCall_OnCompletion_IsCancelledNotHandedOver()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);
        harness.WriteTaskFiles(record.TaskId, MptTaskId);

        harness.Mpt.Script(MptTaskId, 1, 100);
        harness.Mpt.DelayTask(MptTaskId, TimeSpan.FromSeconds(2));

        var cycle = harness.PollOnceAsync();
        (await harness.Mpt.WaitForTaskRequestAsync($"/api/v1/tasks/{MptTaskId}", TimeSpan.FromSeconds(3)))
            .ShouldNotBeNull();

        (await harness.Store.UpdateAsync(record with { CancelRequested = true }, CancellationToken.None))
            .ShouldBeTrue();
        await cycle;

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Cancelled, "a cancel beats the checking hand-off too");
        row.Outcome.ShouldBeNull();
        harness.TaskFilesExist(record.TaskId, MptTaskId).ShouldBeFalse();
        (await harness.ReadPublishedAsync(1, TimeSpan.FromMilliseconds(200))).ShouldBeEmpty();
    }

    [Fact]
    public async Task TerminalFailureWithoutACancel_StillFails()
    {
        // The positive control: the resolving write must not turn an ordinary failure into a cancel.
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);
        harness.Mpt.Script(MptTaskId, -1, 40, failedStage: "audio");

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);
        row.CancelRequested.ShouldBeFalse();
    }
}
