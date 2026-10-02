using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// D6's two lost rules (11 README D6, 05 D5): MPT answering 404 for a task it accepted, and MPT's process start
/// being newer than the task's <c>backend_started_at</c>. Both end in <c>failed</c> + <c>render_lost</c> with a
/// closed detail, and both must work when the systemd seam cannot answer at all.
/// </summary>
public sealed class LostTaskTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    private static readonly string[] Timestamps = ["submitted_at", "started_at", "finished_at"];

    [Fact]
    public async Task MptFourOhFour_RenderLost_MptTaskMissing()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedRunningAtImagesAsync(harness);

        // S12: after a restart MPT's record is simply gone and the task route answers 404.
        harness.Mpt.Forget(MptTaskId);
        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderLost);
        row.Outcome.Detail.ShouldBe(OutcomeMapper.MptTaskMissing);
        row.Outcome.Message.ShouldBe(OutcomeMessages.For(RenderOutcomeCodes.RenderLost));
        row.Stage.ShouldBe(RenderStages.Images, "the last observed stage is kept");
        row.Percent.ShouldBe(40);
        row.MptLastSnapshot!.State.ShouldBeNull("the lost fixture reports the last values with no MPT state");
        row.MptLastSnapshot.Progress.ShouldBe(40);
        row.FinishedAt.ShouldNotBeNull();
        harness.Logs.Contains(OutcomeMapper.MptTaskMissing).ShouldBeTrue();

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.lost.json",
            await harness.StatusJsonAsync(record.TaskId),
            [.. Timestamps, "images"]);
    }

    [Fact]
    public async Task ProcessRestartAfterStart_RenderLost_MptRestarted()
    {
        var acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using var harness = await AdapterHarness.StartAsync();
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            backendStartedAt: acceptedAt,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);

        // MPT still answers for the task, but the process it runs in started after the submit: F06's restart.
        harness.Mpt.Script(MptTaskId, 4, 40);
        harness.FakeStartReader.Available = true;
        harness.FakeStartReader.Value = acceptedAt.AddMinutes(5);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderLost);
        row.Outcome.Detail.ShouldBe(OutcomeMapper.MptRestarted);
        row.Stage.ShouldBe(RenderStages.Images);
        row.Percent.ShouldBe(40);
        row.MptLastSnapshot!.State.ShouldBeNull();
        harness.Mpt.RequestCount.ShouldBe(0, "the process start is enough; the task route is not needed");
    }

    [Fact]
    public async Task StartReaderUnavailable_RestartRuleDisabled404Kept()
    {
        // The real reader against a unit that does not exist: on systemd it exits non-zero, elsewhere it cannot
        // start at all — either way D6's degradation is "rule disabled, 404 rule kept" and one log line.
        await using var harness = await AdapterHarness.StartAsync(
            options => options.MptServiceName = "nts-adapter-tests-does-not-exist.service",
            realStartReader: true);
        var record = await SeedRunningAtImagesAsync(harness);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Running, "an unreadable process start disables the restart rule, not the task");
        row.Percent.ShouldBe(40);
        harness.Logs.Contains("restart rule stays disabled").ShouldBeTrue();

        harness.Mpt.Forget(MptTaskId);
        await harness.PollOnceAsync();

        var lost = await harness.RowAsync(record.TaskId);
        lost.State.ShouldBe(RenderStates.Failed);
        lost.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderLost);
        lost.Outcome.Detail.ShouldBe(OutcomeMapper.MptTaskMissing, "the 404 rule keeps working");
    }

    [Fact]
    public void SystemctlTimestamp_ParsesTheRealShape()
    {
        MptProcessStartReader.ParseTimestamp("ExecMainStartTimestamp=Thu 2026-09-25 09:59:58 UTC\n")
            .ShouldBe(new DateTimeOffset(2026, 9, 25, 9, 59, 58, TimeSpan.Zero));
        MptProcessStartReader.ParseTimestamp("ExecMainStartTimestamp=\n").ShouldBeNull();
        MptProcessStartReader.ParseTimestamp("something else\n").ShouldBeNull();
    }

    private static async Task<RenderTaskRecord> SeedRunningAtImagesAsync(AdapterHarness harness)
    {
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId);
        await harness.SeedAsync(record);

        harness.Mpt.Script(MptTaskId, 4, 40);
        await harness.PollOnceAsync();
        return record;
    }
}
