using System.Net;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The route surface of the mock: the seven <c>render-node.v1</c> routes plus the core's catch-all (11.1.2-T1
/// Tests). The mock maps nothing itself — this proves the core's mapping is what a client sees.
/// </summary>
public sealed class RoutesTests
{
    private static readonly Guid UnknownTask = Guid.CreateVersion7();

    [Fact]
    public async Task Routes_AllSevenMapped_UnknownPath404()
    {
        await using var host = await MockMptTestHost.StartAsync();
        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());

        // The seven routes of 05 §2, each answering something other than the catch-all.
        using (var status = await host.GetAsync($"/v1/renders/{taskId}"))
        {
            status.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var video = await host.GetAsync($"/v1/renders/{taskId}/video"))
        {
            video.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await MockMptTestHost.ReadErrorAsync(video)).Error.Code.ShouldBe(AdapterErrorCodes.OutputNotReady);
        }

        using (var subtitles = await host.GetAsync($"/v1/renders/{taskId}/subtitles"))
        {
            subtitles.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var health = await host.GetAsync(RenderNodeRoutes.Health))
        {
            health.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var cancel = await host.PostAsync($"/v1/renders/{taskId}/cancel", string.Empty))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using (var delete = await host.DeleteAsync($"/v1/renders/{taskId}"))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Anything outside §2 is the contract's route-not-found envelope, never a bare 404.
        using (var unknown = await host.GetAsync("/nope"))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await MockMptTestHost.ReadErrorAsync(unknown)).Error.Code.ShouldBe(AdapterErrorCodes.RouteNotFound);
        }

        // The eighth, contract-only route (12 CR-19) belongs to the core: with no probe backend registered the
        // mock answers fail-closed, which is what 12.8.3-T3 treats as "not a pass".
        using (var probe = await host.PostAsync(RenderNodeRoutes.ProbeImage, "{}"))
        {
            probe.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await MockMptTestHost.ReadErrorAsync(probe)).Error.Code.ShouldBe(AdapterErrorCodes.ProbeUnavailable);
        }

        // The unknown-path answer is stable for a non-UUID id too (the UUID guard runs before any lookup).
        using (var badId = await host.GetAsync("/v1/renders/not-a-uuid"))
        {
            badId.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await MockMptTestHost.ReadErrorAsync(badId)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
        }

        using (var missing = await host.GetAsync($"/v1/renders/{UnknownTask}"))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
