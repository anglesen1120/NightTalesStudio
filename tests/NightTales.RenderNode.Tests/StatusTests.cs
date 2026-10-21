using System.Net;
using System.Text.Json.Nodes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The status invariants of 05.4.1-T2 §4: <c>percent</c> never decreases, 100 appears only once MPT is complete
/// or the task is terminal, a terminal row is final, and the served status carries exactly the documented members
/// — never a node path, MPT URI, script, term or prompt (F11).
/// </summary>
public sealed class StatusTests
{
    [Fact]
    public async Task PercentNeverDecreases()
    {
        var taskId = Records.NewTaskId();
        var renderJobId = Guid.CreateVersion7();
        var stored = Records.Running(taskId, renderJobId, RenderStages.Images, 40);

        stored.Merge(stored with { Percent = 20 }).Percent.ShouldBe(40);
        stored.Merge(stored with { Percent = 50, Stage = RenderStages.Assembling }).Percent.ShouldBe(50);

        // The store applies the same merge, so a stale observation cannot lower a stored row either.
        var store = new InMemoryTaskStore();
        (await store.InsertAsync(stored, CancellationToken.None)).ShouldBeTrue();
        (await store.UpdateAsync(stored with { Percent = 20 }, CancellationToken.None)).ShouldBeTrue();
        var reloaded = await store.TryGetAsync(taskId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        reloaded.Percent.ShouldBe(40);
    }

    [Fact]
    public void Percent100_OnlyWhenTerminal()
    {
        var taskId = Records.NewTaskId();
        var renderJobId = Guid.CreateVersion7();

        // `voice`…`assembling` never reach 100 (§4, §10 T62).
        var assembling = Records.Running(taskId, renderJobId, RenderStages.Assembling, 75);
        assembling.Merge(assembling with { Percent = 100 }).Percent.ShouldBe(99);

        // `checking` is the adapter's own post-processing stage and may.
        var checking = Records.Running(taskId, renderJobId, RenderStages.Checking, 99);
        checking.Merge(checking with { Percent = 100 }).Percent.ShouldBe(100);

        // A terminal row is final: a late observation cannot reopen it or lower its percent.
        var succeeded = Records.Succeeded(taskId, renderJobId);
        var late = succeeded.Merge(succeeded with { State = RenderStates.Running, Percent = 50 });
        late.State.ShouldBe(RenderStates.Succeeded);
        late.Percent.ShouldBe(100);

        // cancel_requested is one-way: once a cancel lands, a stale observation can never clear it.
        var cancelled = Records.Running(taskId, renderJobId) with { CancelRequested = true };
        Records.Running(taskId, renderJobId).Merge(cancelled).CancelRequested.ShouldBeTrue();
        cancelled.Merge(Records.Running(taskId, renderJobId)).CancelRequested.ShouldBeTrue(
            "a stale poller snapshot must not revert a cancel that already landed (05 D4)");
    }

    [Fact]
    public async Task StaleObservation_NeverLowersPercentOrStage()
    {
        var taskId = Records.NewTaskId();
        var renderJobId = Guid.CreateVersion7();

        // A `/cancel` snapshots the row before `checking`, then writes it back after the poller already
        // published checking/100: merging that stale observation must not lower percent (CR-4) or move the
        // stage backwards.
        var checking = Records.Running(taskId, renderJobId, RenderStages.Checking, 100);
        var stale = Records.Running(taskId, renderJobId, RenderStages.Assembling, 99);

        var merged = checking.Merge(stale);

        merged.Percent.ShouldBe(100);
        merged.Stage.ShouldBe(RenderStages.Checking);

        // The store applies the same merge, so a stale write through the seam is bounded too.
        var store = new InMemoryTaskStore();
        (await store.InsertAsync(checking, CancellationToken.None)).ShouldBeTrue();
        (await store.UpdateAsync(stale, CancellationToken.None)).ShouldBeTrue();
        var reloaded = await store.TryGetAsync(taskId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        reloaded.Percent.ShouldBe(100);
        reloaded.Stage.ShouldBe(RenderStages.Checking);
    }

    [Fact]
    public async Task Status_SerializesNoPathOrPrompt()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(
            taskId,
            Guid.CreateVersion7(),
            check: Records.CheckBlock(),
            images: Records.ImagesBlock()));

        var response = await host.GetAsync($"/v1/renders/{taskId}");
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        raw.ShouldNotContain("/var/lib/nts");
        raw.ShouldNotContain("storage/tasks");
        raw.ShouldNotContain("http://");

        var members = JsonNode.Parse(raw)!.AsObject().Select(member => member.Key).ToArray();
        members.ShouldBe<string>(
        [
            "schema",
            "task_id",
            "render_job_id",
            "state",
            "stage",
            "percent",
            "cancel_requested",
            "submitted_at",
            "started_at",
            "finished_at",
            "outcome",
            "output",
            "check",
            "images",
            "mpt",
        ]);
    }

    [Fact]
    public async Task Status_IsTheStoredRow()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        var renderJobId = Guid.CreateVersion7();
        await host.SeedAsync(Records.Failed(taskId, renderJobId, RenderOutcomeCodes.TtsFailed));

        var response = await host.GetAsync($"/v1/renders/{taskId}");
        var status = await RenderNodeTestHost.ReadAsync<RenderStatusV1>(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        status.TaskId.ShouldBe(taskId);
        status.RenderJobId.ShouldBe(renderJobId);
        status.State.ShouldBe(RenderStates.Failed);
        status.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);
        status.Outcome!.Message.ShouldBe(OutcomeMessages.For(RenderOutcomeCodes.TtsFailed));
        status.Output.ShouldBeNull();
        // Store-only: a poll never asks the backend or the relay for anything.
        host.Backend.OpenOutputCount.ShouldBe(0);
        host.Backend.SubmitCount.ShouldBe(0);
        // The adapter's own task id is internal (05 C3) and never appears in a status.
        status.Mpt!.State.ShouldBe(-1);
    }
}
