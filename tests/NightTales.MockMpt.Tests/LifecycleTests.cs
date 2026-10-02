using System.Net;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// Cancel and delete semantics (11.1.2-T1 Tests, 05 D4): cancel is idempotent and releases the image plan,
/// delete is refused while the task still runs, and a mock restart loses every task (the intended in-memory
/// loss that becomes the Worker's <c>render_lost</c>).
/// </summary>
public sealed class LifecycleTests
{
    [Fact]
    public async Task Cancel_Idempotent_ReleasesPlan()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 60 });
        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        var id = Guid.Parse(taskId);

        host.PlanSink.BuildReport(id).ShouldNotBeNull("the plan is registered before the backend submits");

        using (var first = await host.PostAsync($"/v1/renders/{taskId}/cancel", string.Empty))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        host.PlanSink.BuildReport(id).ShouldBeNull("cancel releases the image plan (F08)");

        var cancelled = await host.StatusAsync(taskId);
        cancelled.State.ShouldBe(RenderStates.Cancelled);
        cancelled.Stage.ShouldBe(RenderStages.Done);
        cancelled.Percent.ShouldBe(100);
        cancelled.CancelRequested.ShouldBeTrue();
        cancelled.Outcome.ShouldBeNull();

        // A second cancel on a terminal task is the 200 no-op of the contract, not another 202.
        using (var second = await host.PostAsync($"/v1/renders/{taskId}/cancel", string.Empty))
        {
            second.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await MockMptTestHost.ReadAsync<RenderStatusV1>(second)).State.ShouldBe(RenderStates.Cancelled);
        }
    }

    [Fact]
    public async Task Delete_WhileRunning_409Busy()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 60 });
        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());

        using (var busy = await host.DeleteAsync($"/v1/renders/{taskId}"))
        {
            busy.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await MockMptTestHost.ReadErrorAsync(busy)).Error.Code.ShouldBe(AdapterErrorCodes.TaskBusy);
        }

        using (var cancel = await host.PostAsync($"/v1/renders/{taskId}/cancel", string.Empty))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using (var deleted = await host.DeleteAsync($"/v1/renders/{taskId}"))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The row is kept with `cleaned_at` for the 7-day window, so a re-delete is the same 204.
        using (var again = await host.DeleteAsync($"/v1/renders/{taskId}"))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Restart_StoreLosesTasks_PollReturns404()
    {
        string taskId;
        await using (var first = await MockMptTestHost.StartAsync(
                         MockMptTestHost.DefaultOptions with { RenderSeconds = 60 }))
        {
            taskId = await first.SubmitAsync(SubmitBodies.LandscapeFresh());
        }

        await using var restarted = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 60 });

        using var response = await restarted.GetAsync($"/v1/renders/{taskId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
    }
    [Fact]
    public async Task Cancel_RunningFixtureReplay_ThenDelete()
    {
        // A frozen (fixture-replayed) non-terminal task still honours the lifecycle: cancel flips it to
        // cancelled/100, delete then removes it — the fixture state is not an escape from 05 D4's rules.
        await using var host = await MockMptTestHost.StartAsync();
        var body = SubmitBodies.Build(
            SubmitBodies.Landscape,
            script: "Replay __fixture_render-status.v1.running-voice__ here.");
        var taskId = await host.SubmitAsync(body);

        using (var cancel = await host.PostAsync($"/v1/renders/{taskId}/cancel", string.Empty))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var cancelled = await host.StatusAsync(taskId);
        cancelled.State.ShouldBe(RenderStates.Cancelled);
        cancelled.Percent.ShouldBe(100);

        using var deleted = await host.DeleteAsync($"/v1/renders/{taskId}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
