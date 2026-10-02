using System.Net;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// Idempotency by <c>render_job_id</c> + the raw body's sha256 (05.4.1-T2 §3): a byte-identical replay answers
/// 200 with the stored <c>{task_id, state}</c> and never submits twice; the same job id with a different body is
/// the 409 the contract names.
/// </summary>
public sealed class IdempotencyTests
{
    [Fact]
    public async Task SameJobSameBody_200()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var body = SubmitBodies.From(Guid.CreateVersion7());

        var first = await host.PostAsync(RenderNodeRoutes.Renders, body);
        var replay = await host.PostAsync(RenderNodeRoutes.Renders, body);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstAccepted = await RenderNodeTestHost.ReadAsync<AdapterAcceptedV1>(first);
        var replayed = await RenderNodeTestHost.ReadAsync<AdapterAcceptedV1>(replay);
        replayed.TaskId.ShouldBe(firstAccepted.TaskId);
        replayed.State.ShouldBe(RenderStates.Queued);
        host.Backend.SubmitCount.ShouldBe(1);
        host.Store.InsertCount.ShouldBe(1);
    }

    [Fact]
    public async Task SameJobOtherBody_409()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var renderJobId = Guid.CreateVersion7();

        var first = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(renderJobId, SubmitBodies.LandscapeVi));
        var conflict = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(renderJobId, SubmitBodies.NoMusic));

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RenderNodeTestHost.ReadErrorAsync(conflict)).Error.Code.ShouldBe(AdapterErrorCodes.IdempotencyConflict);
        host.Backend.SubmitCount.ShouldBe(1);
    }

    [Fact]
    public async Task DifferentJobIds_AreBothAccepted()
    {
        // The idempotency key is the pair (render_job_id, body sha256): the same field values under two job ids
        // are two renders, so the node must have two slots for them.
        await using var host = await RenderNodeTestHost.StartAsync(options => options.MaxConcurrentTasks = 2);

        var first = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));
        var second = await host.PostAsync(RenderNodeRoutes.Renders, SubmitBodies.From(Guid.CreateVersion7()));

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        host.Backend.SubmitCount.ShouldBe(2);
    }
}
