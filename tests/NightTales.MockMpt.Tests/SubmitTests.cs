using System.Net;
using System.Text.Json.Nodes;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The submit pipeline the mock inherits from the core (11.1.2-T1 Tests): the accepted envelope, idempotency by
/// <c>render_job_id</c> + body hash and the capacity gate with its documented <c>Retry-After</c>.
/// </summary>
public sealed class SubmitTests
{
    [Fact]
    public async Task Submit_ValidBody_202Queued()
    {
        await using var host = await MockMptTestHost.StartAsync();

        using var response = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.LandscapeFresh());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await MockMptTestHost.ReadAsync<AdapterAcceptedV1>(response);
        accepted.State.ShouldBe(RenderStates.Queued);
        accepted.TaskId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Submit_Replay_200SameTaskId_And_DifferentBody_409()
    {
        await using var host = await MockMptTestHost.StartAsync();
        var jobId = Guid.CreateVersion7();
        var body = SubmitBodies.Build(SubmitBodies.Landscape, renderJobId: jobId);

        var taskId = await host.SubmitAsync(body);

        // The byte-identical replay answers 200 with the stored task (05 D4).
        using (var replay = await host.PostAsync(RenderNodeRoutes.Renders, body))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await MockMptTestHost.ReadAsync<AdapterAcceptedV1>(replay)).TaskId.ToString().ShouldBe(taskId);
        }

        // The same job id with another body is the conflict.
        var otherBody = SubmitBodies.Build(SubmitBodies.Landscape, renderJobId: jobId, script: "Một câu khác.");
        using (var conflict = await host.PostAsync(RenderNodeRoutes.Renders, otherBody))
        {
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await MockMptTestHost.ReadErrorAsync(conflict)).Error.Code.ShouldBe(AdapterErrorCodes.IdempotencyConflict);
        }
    }

    [Fact]
    public async Task Submit_CapacityFull_429WithRetryAfter30()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { MaxConcurrentTasks = 1, RenderSeconds = 60 });

        await host.SubmitAsync(SubmitBodies.LandscapeFresh());

        using var response = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.PortraitFresh());

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBe(30);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.CapacityFull);
    }
    [Fact]
    public async Task Submit_InvalidAspect_400ContractViolation()
    {
        // An aspect outside the enum is the validator's 400 at submit (Failure handling), never a 500 at
        // download time.
        await using var host = await MockMptTestHost.StartAsync();
        var node = JsonNode.Parse(SubmitBodies.LandscapeFresh())!.AsObject();
        node["mpt"]!["video_aspect"] = "4:3";

        using var response = await host.PostAsync(RenderNodeRoutes.Renders, node.ToJsonString());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.ContractViolation);
    }

    [Fact]
    public async Task Submit_BodyOverTheCap_413BodyTooLarge()
    {
        // The core's 2,097,152-byte cap (05 D15 S2) applies through the mock: an oversized submit is the
        // documented envelope, never a Kestrel disconnect. `Expect: 100-continue` lets the server refuse by
        // the declared Content-Length before the client streams the body.
        await using var host = await MockMptTestHost.StartAsync();
        var oversized = new string('x', (int)RenderNodeServerOptions.DefaultMaxBodyBytes + 1);
        using var request = new HttpRequestMessage(HttpMethod.Post, RenderNodeRoutes.Renders)
        {
            Content = MockMptTestHost.Json(oversized),
        };
        request.Headers.ExpectContinue = true;
        request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, MockMptTestHost.ApiKey);

        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe((HttpStatusCode)413);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.BodyTooLarge);
    }
}
