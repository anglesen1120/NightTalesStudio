using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The D4 stage mapping and the poller's invariants (11 README D6, 05 D4/D5): progress → stage/percent,
/// monotonic percent, 100 only with a terminal-or-checking state, one hand-over per completion, and a cadence
/// that follows <c>Adapter:PollSeconds</c>.
/// </summary>
public sealed class MptTaskPollerTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    private static readonly string[] Timestamps = ["submitted_at", "started_at", "finished_at"];

    [Fact]
    public async Task Progress5_Voice()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 5);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running);
        row.Stage.ShouldBe(RenderStages.Voice);
        row.Percent.ShouldBe(5);
        row.MptLastSnapshot!.State.ShouldBe(4);
        row.MptLastSnapshot.Progress.ShouldBe(5);

        // 5, 10 and 20 are one band (C7); 20 is the value the frozen fixture carries.
        harness.Mpt.Script(MptTaskId, 4, 20);
        await harness.PollOnceAsync();

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.running-voice.json",
            await harness.StatusJsonAsync(record.TaskId),
            Timestamps);
    }

    [Fact]
    public async Task Progress30_Subtitles()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 30);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running);
        row.Stage.ShouldBe(RenderStages.Subtitles);
        row.Percent.ShouldBe(30);
    }

    [Fact]
    public async Task Progress40_Images()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 40);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running);
        row.Stage.ShouldBe(RenderStages.Images);
        row.Percent.ShouldBe(40);
    }

    [Fact]
    public async Task Progress50_Assembling()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 50);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running);
        row.Stage.ShouldBe(RenderStages.Assembling);
        row.Percent.ShouldBe(50);

        // 50…99 is the same band (05 D4) — 75 is the value the assembly-failure fixture carries.
        harness.Mpt.Script(MptTaskId, 4, 75);
        await harness.PollOnceAsync();

        row = await harness.RowAsync(record.TaskId);
        row.Stage.ShouldBe(RenderStages.Assembling);
        row.Percent.ShouldBe(75);
    }

    [Fact]
    public async Task Progress100_Checking()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 40);
        await harness.PollOnceAsync();

        harness.Mpt.Script(MptTaskId, 1, 100);
        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running);
        row.Stage.ShouldBe(RenderStages.Checking);
        row.Percent.ShouldBe(100, "05 D4: 100 is allowed at checking");
        row.FinishedAt.ShouldBeNull("T3's post-processor owns the terminal transition");

        var published = await harness.ReadPublishedAsync(1, TimeSpan.FromSeconds(2));
        published.Count.ShouldBe(1);
        published[0].TaskId.ShouldBe(record.TaskId);
        published[0].Stage.ShouldBe(RenderStages.Checking);

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.checking.json",
            await harness.StatusJsonAsync(record.TaskId),
            [.. Timestamps, "images"]);

        // The check is in flight, so there is nothing new to ask MPT and no second hand-over.
        var requests = harness.Mpt.TaskRequests.Count;
        await harness.PollOnceAsync();
        harness.Mpt.TaskRequests.Count.ShouldBe(requests);
        (await harness.ReadPublishedAsync(1, TimeSpan.FromMilliseconds(200))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Percent_NeverDecreases()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);

        harness.Mpt.Script(MptTaskId, 4, 50);
        await harness.PollOnceAsync();
        (await harness.RowAsync(record.TaskId)).Percent.ShouldBe(50);

        harness.Mpt.Script(MptTaskId, 4, 99);
        await harness.PollOnceAsync();
        (await harness.RowAsync(record.TaskId)).Percent.ShouldBe(99, "a running task never publishes 100");

        // A stale observation must not move the stored progress or stage backwards.
        harness.Mpt.Script(MptTaskId, 4, 20);
        await harness.PollOnceAsync();
        var row = await harness.RowAsync(record.TaskId);
        row.Percent.ShouldBe(99);
        row.Stage.ShouldBe(RenderStages.Assembling);
        row.MptLastSnapshot!.Progress.ShouldBe(20, "the raw snapshot keeps what MPT said (05 D4)");

        harness.Mpt.Script(MptTaskId, 1, 100);
        await harness.PollOnceAsync();
        (await harness.RowAsync(record.TaskId)).Percent.ShouldBe(100);
    }

    [Fact]
    public async Task TerminalState_OnlyOnce()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);

        harness.Mpt.Script(MptTaskId, 4, 20);
        await harness.PollOnceAsync();

        harness.Mpt.Script(MptTaskId, -1, 20, failedStage: "audio");
        await harness.PollOnceAsync();
        var failed = await harness.RowAsync(record.TaskId);
        failed.State.ShouldBe(RenderStates.Failed);
        failed.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);

        var requests = harness.Mpt.TaskRequests.Count;
        harness.Mpt.Script(MptTaskId, 4, 50);
        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Failed, "a terminal row is final");
        row.Percent.ShouldBe(20);
        harness.Mpt.TaskRequests.Count.ShouldBe(requests, "a terminal task is not polled again");
    }

    [Fact]
    public async Task PollCadence_TwoSeconds()
    {
        new AdapterOptions().PollSeconds.ShouldBe(2, "D6 pins the cadence at 2 s");

        await using var harness = await AdapterHarness.StartAsync(options => options.PollSeconds = 1);
        await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 4, 20);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await harness.Poller.StartAsync(cancellation.Token);
        try
        {
            (await harness.Mpt.WaitForTaskRequestCountAsync(1, TimeSpan.FromSeconds(3)))
                .ShouldBeTrue("the first cycle must poll the task");
            (await harness.Mpt.WaitForTaskRequestCountAsync(2, TimeSpan.FromSeconds(3)))
                .ShouldBeTrue("the poller must repeat at Adapter:PollSeconds");
        }
        finally
        {
            await harness.Poller.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The row <c>POST /v1/renders</c> leaves behind right after <c>RecordBackendAcceptanceAsync</c>.</summary>
    private static async Task<RenderTaskRecord> SeedSubmittedAsync(AdapterHarness harness)
    {
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId);
        await harness.SeedAsync(record);
        return record;
    }
}
