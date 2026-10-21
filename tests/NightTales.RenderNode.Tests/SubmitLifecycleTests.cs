using System.Net;
using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The submit path's lifecycle edges of 11.3.5-T1: a cancel accepted while the backend submit is still in flight
/// must survive the submit result (CR-2), must survive the failed row a refused submit writes (DELTA-NEW-1), and
/// a submit aborted by the client must not leave a non-terminal row holding the node's only slot (CR-3).
/// </summary>
public sealed class SubmitLifecycleTests
{
    [Fact]
    public async Task CancelDuringPendingSubmit_SurvivesTheSubmitResult()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Backend.SubmitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = SubmitBodies.From(Guid.CreateVersion7());

        var pending = host.PostAsync(RenderNodeRoutes.Renders, body);
        await host.Backend.SubmitEntered.WaitAsync(TestContext.Current.CancellationToken);

        // While the first submit is in flight the row is already visible, so a replay answers its task id (§3).
        var replay = await host.PostAsync(RenderNodeRoutes.Renders, body);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var taskId = (await RenderNodeTestHost.ReadAsync<AdapterAcceptedV1>(replay)).TaskId;

        var cancel = await host.PostAsync($"/v1/renders/{taskId}/cancel", "{}");
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        host.Backend.SubmitGate.SetResult();
        var answered = await pending;
        answered.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var stored = await host.Store.StoredAsync(taskId);
        stored.ShouldNotBeNull();
        stored!.BackendTaskId.ShouldBe(host.Backend.BackendTaskId);
        stored.CancelRequested.ShouldBeTrue("the cancel accepted during the submit must not be overwritten by it");
        host.PlanSink.ReleaseCount.ShouldBe(1);
    }

    [Fact]
    public async Task CancelDuringRefusedSubmit_KeptOnTheFailedRow()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Backend.SubmitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Backend.SubmitError = Error.Failure("mpt_unreachable");
        var body = SubmitBodies.From(Guid.CreateVersion7());

        var pending = host.PostAsync(RenderNodeRoutes.Renders, body);
        await host.Backend.SubmitEntered.WaitAsync(TestContext.Current.CancellationToken);

        // The pending row is visible, so a replay hands back its task id (§3) and a cancel can land on it.
        var replay = await host.PostAsync(RenderNodeRoutes.Renders, body);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var taskId = (await RenderNodeTestHost.ReadAsync<AdapterAcceptedV1>(replay)).TaskId;

        var cancel = await host.PostAsync($"/v1/renders/{taskId}/cancel", "{}");
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The backend then refuses the submit: the row is finished as failed, but the pre-submit snapshot the
        // cleanup merges from has CancelRequested=false, so the accepted cancel must not be reset by it.
        host.Backend.SubmitGate.SetResult();
        var answered = await pending;
        answered.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var status = await host.GetAsync($"/v1/renders/{taskId}");
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonNode.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        json["state"]!.GetValue<string>().ShouldBe(RenderStates.Failed);
        json["cancel_requested"]!.GetValue<bool>().ShouldBeTrue("the cancel accepted during the pending submit must survive the refused submit");

        var stored = await host.Store.StoredAsync(taskId);
        stored.ShouldNotBeNull();
        stored!.CancelRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task AbortedSubmit_FinishesTheRowAndFreesTheSlot()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Backend.SubmitBlocksUntilCancelled = true;
        using var cts = new CancellationTokenSource();

        var pending = host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()), cts.Token);
        await host.Backend.SubmitEntered.WaitAsync(TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(pending);

        await WaitForNonTerminalAsync(host, expected: 0);

        // The slot the aborted submit held is free again: a fresh job is accepted, not 429'd.
        host.Backend.SubmitBlocksUntilCancelled = false;
        var next = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));
        next.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        host.PlanSink.ReleaseCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task WaitForNonTerminalAsync(RenderNodeTestHost host, int expected)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            if (await host.Store.CountNonTerminalAsync(CancellationToken.None) == expected)
            {
                return;
            }

            await Task.Delay(25);
        }

        (await host.Store.CountNonTerminalAsync(CancellationToken.None)).ShouldBe(expected);
    }
}
