using System.Net;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// Cancel and delete (11 README D6, 05 D4/C11/D15 P4): a cancel marks and never calls MPT, a cancelled task is
/// never <c>succeeded</c> and loses its files at once, a running delete is <c>409 adapter_task_busy</c>, and a
/// finished delete removes MPT's task, the task dirs and both relay files before answering 204.
/// </summary>
public sealed class CancelDeleteTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    private static readonly string[] Timestamps = ["submitted_at", "started_at", "finished_at"];

    [Fact]
    public async Task Cancel_MarksOnly_NoMptCall()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);
        harness.Mpt.Script(MptTaskId, 4, 40);

        var response = await harness.SendAsync(HttpMethod.Post, $"/v1/renders/{record.TaskId}/cancel");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var row = await harness.RowAsync(record.TaskId);
        row.CancelRequested.ShouldBeTrue();
        row.State.ShouldBe(RenderStates.Running, "MPT still runs it; the poller finalises on its terminal state");
        harness.Mpt.RequestCount.ShouldBe(0, "MPT has no cancel route (05 D4/F08)");
        harness.PlanSink.Released.ShouldContain(record.TaskId);

        // A second cancel is idempotent and still does not call MPT.
        var again = await harness.SendAsync(HttpMethod.Post, $"/v1/renders/{record.TaskId}/cancel");
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        harness.Mpt.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelled_NeverSucceeded_TaskDirRemoved()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Assembling,
            percent: 90,
            cancelRequested: true);
        await harness.SeedAsync(record);
        harness.WriteTaskFiles(record.TaskId, MptTaskId);

        // MPT reaches its terminal state (state 1 = complete): the cancel wins over the success.
        harness.Mpt.Script(MptTaskId, 1, 100);
        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Cancelled);
        row.Stage.ShouldBe(RenderStages.Done);
        row.Percent.ShouldBe(100);
        row.Outcome.ShouldBeNull("05 D4: a cancelled task carries no outcome");
        harness.TaskFilesExist(record.TaskId, MptTaskId).ShouldBeFalse(
            "05 D15 P4: a cancelled task's dir and relay files go at once");
        (await harness.ReadPublishedAsync(1, TimeSpan.FromMilliseconds(200)))
            .ShouldBeEmpty("a cancelled task must never reach the post-processor");

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.cancelled.json",
            await harness.StatusJsonAsync(record.TaskId),
            [.. Timestamps, "images"]);
    }

    [Fact]
    public async Task Delete_Running_409()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Running,
            stage: RenderStages.Images,
            percent: 40);
        await harness.SeedAsync(record);
        harness.Mpt.Script(MptTaskId, 4, 40);

        var response = await harness.SendAsync(HttpMethod.Delete, $"/v1/renders/{record.TaskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AdapterHarness.ReadErrorCodeAsync(response)).ShouldBe(AdapterErrorCodes.TaskBusy);
        harness.Mpt.RequestCount.ShouldBe(0, "C11/F08: MPT refuses to delete a running task, so it is not asked");
        (await harness.RowAsync(record.TaskId)).CleanedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_Succeeded_RemovesTaskDirAndRelayFiles()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Succeeded,
            stage: RenderStages.Done,
            percent: 100,
            output: new RenderOutputV1(new RenderFileV1(98_100_000, "sha-video", null), null, 301.37));
        await harness.SeedAsync(record);
        harness.WriteTaskFiles(record.TaskId, MptTaskId);

        var response = await harness.SendAsync(HttpMethod.Delete, $"/v1/renders/{record.TaskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        harness.Mpt.Requests.Count(request => request.Method == "DELETE").ShouldBe(1);
        harness.Mpt.Requests.Single(request => request.Method == "DELETE").TaskId
            .ShouldBe(record.TaskId.ToString("D"));

        var row = await harness.RowAsync(record.TaskId);
        row.CleanedAt.ShouldNotBeNull();
        row.State.ShouldBe(RenderStates.Succeeded);
        harness.TaskFilesExist(record.TaskId, MptTaskId).ShouldBeFalse(
            "the MPT task dir, the adapter's report dir and both relay files are gone");

        // 05 D4: the row keeps cleaned_at for the 7-day purge, and a second delete is a 204 that touches nothing.
        var again = await harness.SendAsync(HttpMethod.Delete, $"/v1/renders/{record.TaskId}");
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        harness.Mpt.Requests.Count(request => request.Method == "DELETE").ShouldBe(1);
    }

    [Fact]
    public async Task Delete_MptAlreadyForgotTheTask_Still204()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId,
            state: RenderStates.Failed,
            stage: RenderStages.Images,
            percent: 40,
            outcome: new RenderOutcomeV1(
                RenderOutcomeCodes.RenderLost,
                OutcomeMapper.MptTaskMissing,
                OutcomeMessages.For(RenderOutcomeCodes.RenderLost)));
        await harness.SeedAsync(record);
        harness.Mpt.DeleteStatus = (int)HttpStatusCode.NotFound;

        var response = await harness.SendAsync(HttpMethod.Delete, $"/v1/renders/{record.TaskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "S13: a 404 means the goal is already met");
        (await harness.RowAsync(record.TaskId)).CleanedAt.ShouldNotBeNull();
    }
}
