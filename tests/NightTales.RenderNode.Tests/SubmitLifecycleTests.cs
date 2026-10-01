using System.Net;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The submit path's lifecycle edges of 11.3.5-T1: a cancel accepted while the backend submit is still in flight
/// must survive the submit result (CR-2), and a submit aborted by the client must not leave a non-terminal row
/// holding the node's only slot (CR-3).
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
