using System.Net;
using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// Cancel and delete of 05.4.1-T2 §2: cancel is idempotent and releases the image plan exactly once, delete keeps
/// <c>cleaned_at</c> so a re-delete is a 204, and a delete while MPT still runs is the task-busy 409 (F08, C11).
/// </summary>
public sealed class CancelDeleteTests
{
    [Fact]
    public async Task Cancel_Idempotent_ReleasesPlanOnce()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Running(taskId, Guid.CreateVersion7()));

        var first = await host.PostAsync($"/v1/renders/{taskId}/cancel", "{}");
        var second = await host.PostAsync($"/v1/renders/{taskId}/cancel", "{}");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var status = await RenderNodeTestHost.ReadAsync<RenderStatusV1>(second);
        status.CancelRequested.ShouldBeTrue();
        status.State.ShouldBe(RenderStates.Running);
        host.PlanSink.ReleaseCount.ShouldBe(1);
        host.Backend.CancelCount.ShouldBe(1);

        var stored = await host.Store.StoredAsync(taskId);
        stored.ShouldNotBeNull();
        stored.CancelRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancel_TerminalTask_200NoOp()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7()));

        var response = await host.PostAsync($"/v1/renders/{taskId}/cancel", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await RenderNodeTestHost.ReadAsync<RenderStatusV1>(response);
        status.State.ShouldBe(RenderStates.Succeeded);
        status.CancelRequested.ShouldBeFalse();
        host.PlanSink.ReleaseCount.ShouldBe(0);
        host.Backend.CancelCount.ShouldBe(0);
    }

    [Fact]
    public async Task Delete_204_Then204_CleanedAtKept()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7()));

        var first = await host.DeleteAsync($"/v1/renders/{taskId}");
        var second = await host.DeleteAsync($"/v1/renders/{taskId}");

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        host.Backend.DeleteCount.ShouldBe(1);
        host.PlanSink.ReleaseCount.ShouldBe(1);

        // The row survives for the 7-day purge window (D5/D15 P4); only cleaned_at is set.
        var stored = await host.Store.StoredAsync(taskId);
        stored.ShouldNotBeNull();
        stored.CleanedAt.ShouldNotBeNull();
        stored.State.ShouldBe(RenderStates.Succeeded);
    }

    [Fact]
    public async Task Delete_Running_409_AdapterTaskBusy()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Running(taskId, Guid.CreateVersion7()));
        host.Backend.DeleteError = Error.Conflict(AdapterErrorCodes.TaskBusy);

        var response = await host.DeleteAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskBusy);
        var stored = await host.Store.StoredAsync(taskId);
        stored.ShouldNotBeNull();
        stored.CleanedAt.ShouldBeNull();
        host.PlanSink.ReleaseCount.ShouldBe(0);
    }

    [Fact]
    public async Task Delete_UpdateConflict_IsRetriedOnce()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7()));
        host.Store.FailFirstUpdate = true;

        var response = await host.DeleteAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        host.Store.UpdateCount.ShouldBeGreaterThanOrEqualTo(2);
        var stored = await host.Store.StoredAsync(taskId);
        stored.ShouldNotBeNull();
        stored.CleanedAt.ShouldNotBeNull();
    }
}
