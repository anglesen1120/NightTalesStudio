using System.Net;
using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The submit contract over HTTP (05.4.1-T2 §3): a valid body is accepted and stores nothing but a queued row, a
/// duplicate property, an unknown member and a missing key are the 400 the acceptance names, and a misnamed key
/// never comes back in the response.
/// </summary>
public sealed class SubmitContractTests
{
    [Fact]
    public async Task ValidBody_202_QueuedAndPlanRegistered()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var renderJobId = Guid.CreateVersion7();

        var response = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(renderJobId));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await RenderNodeTestHost.ReadAsync<AdapterAcceptedV1>(response);
        accepted.State.ShouldBe(RenderStates.Queued);
        accepted.TaskId.ShouldNotBe(Guid.Empty);
        host.Backend.SubmitCount.ShouldBe(1);
        host.Backend.LastSubmittedTaskId.ShouldBe(accepted.TaskId);
        host.Backend.LastSubmittedFields.ShouldNotBeNull();
        host.PlanSink.RegisterCount.ShouldBe(1);
        host.PlanSink.LastPlan!.MaxImages.ShouldBe(4);
        var stored = await host.Store.StoredAsync(accepted.TaskId);
        stored.ShouldNotBeNull();
    }

    [Fact]
    public async Task DuplicateProperty_400_AndNothingStored()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.From(Guid.CreateVersion7())
            .Replace("\"schema\":\"render-submit.v1\"", "\"schema\":\"render-submit.v1\",\"schema\":\"render-submit.v1\"", StringComparison.Ordinal);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.ContractViolation);
        host.Store.InsertCount.ShouldBe(0);
        host.Backend.SubmitCount.ShouldBe(0);
    }

    [Fact]
    public async Task UnknownTopLevelMember_400_AndNothingStored()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.From(Guid.CreateVersion7())
            .Replace("\"schema\":\"render-submit.v1\"", "\"schema\":\"render-submit.v1\",\"unknown\":1", StringComparison.Ordinal);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.ContractViolation);
        host.Store.InsertCount.ShouldBe(0);
    }

    [Fact]
    public async Task MissingKey_400_WithTheFieldAndRule()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), "video_terms", null, remove: true);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var envelope = await RenderNodeTestHost.ReadErrorAsync(response);
        envelope.Error.Code.ShouldBe(AdapterErrorCodes.ContractViolation);
        envelope.Error.Errors.ShouldNotBeNull();
        envelope.Error.Errors.ShouldContain(error => error.Field == "mpt.video_terms" && error.Rule == SubmitRules.MissingKey);
    }

    [Fact]
    public async Task MisnamedKey_IsRedactedAndNeverEchoed()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), new string('k', 4096), JsonValue.Create(1));

        var response = await host.PostAsync(RenderNodeRoutes.Renders, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Length.ShouldBeLessThan(2048, "the 4 KB key name must not be echoed back (§10 T69)");
        var envelope = await RenderNodeTestHost.ReadErrorAsync(response);
        envelope.Error.Errors!.ShouldContain(error => error.Field == "mpt.<redacted>" && error.Rule == SubmitRules.UnknownKey);
    }

    [Fact]
    public async Task HealthNotOk_503_AndNothingStored()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Health.Health = RenderNodeHealthFixture.Error(NodeHealthReasons.TtsProxyNotOk);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.NodeUnhealthy);
        host.Store.InsertCount.ShouldBe(0);
        host.Backend.SubmitCount.ShouldBe(0);
    }

    [Fact]
    public async Task HealthSourceThrows_503_AndNothingStored()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Health.Throws = true;

        var response = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.NodeUnhealthy);
        host.Store.InsertCount.ShouldBe(0);
    }

    [Fact]
    public async Task BackendRefusesSubmit_503_FailedRowAndPlanReleased()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Backend.SubmitError = Error.Failure("mpt_unreachable");

        var response = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.NodeUnhealthy);
        host.PlanSink.ReleaseCount.ShouldBe(1);

        // The row stays as §4's failure rule describes it: failed with render_node_error, so a later poll and the
        // next submit both see a task that is over, and the plan is not registered any more.
        var stored = await host.Store.StoredAsync(host.Backend.LastSubmittedTaskId!.Value);
        stored.ShouldNotBeNull();
        stored.State.ShouldBe(RenderStates.Failed);
        stored.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        stored.FinishedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task NullMember_400_NotA500_AndNothingStored()
    {
        // CR-0: a body with every key present but `video_subject` null used to reach the validator's range
        // group and throw; it must be the same 400 contract violation as any other type fault.
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.WithMpt(Guid.CreateVersion7(), "video_subject", value: null);

        var response = await host.PostAsync(RenderNodeRoutes.Renders, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var envelope = await RenderNodeTestHost.ReadErrorAsync(response);
        envelope.Error.Code.ShouldBe(AdapterErrorCodes.ContractViolation);
        envelope.Error.Errors!.ShouldContain(error => error.Field == "mpt.video_subject" && error.Rule == SubmitRules.WrongType);
        host.Store.InsertCount.ShouldBe(0);
        host.Backend.SubmitCount.ShouldBe(0);
    }
}
