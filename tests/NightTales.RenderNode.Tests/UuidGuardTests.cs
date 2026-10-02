using System.Net;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fakes;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The <c>{task_id}</c> guard of 05.4.1-T2 §1 on every task-scoped route: a non-UUID id is
/// <c>404 adapter_task_unknown</c> and, crucially, no store lookup happened — the guard is what keeps an
/// unauthenticated-shaped guess from probing the store for free.
/// </summary>
public sealed class UuidGuardTests
{
    [Theory]
    [InlineData("/v1/renders/not-a-uuid")]
    [InlineData("/v1/renders/11111111")]
    [InlineData("/v1/renders/0192f3c1-8a4b-7c2d-9e10-3f5a6b7c8d9")]
    [InlineData("/v1/renders/not-a-uuid/video")]
    [InlineData("/v1/renders/not-a-uuid/subtitles")]
    public async Task NonUuid_404_WithoutStoreLookup(string path)
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
        host.Store.TryGetCount.ShouldBe(0);
        host.Backend.OpenOutputCount.ShouldBe(0);
    }

    [Fact]
    public async Task NonUuidCancel_404_WithoutStoreLookup()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.PostAsync("/v1/renders/not-a-uuid/cancel", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
        host.Store.TryGetCount.ShouldBe(0);
        host.Backend.CancelCount.ShouldBe(0);
    }

    [Fact]
    public async Task NonUuidDelete_404_WithoutStoreLookup()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.DeleteAsync("/v1/renders/not-a-uuid");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
        host.Store.TryGetCount.ShouldBe(0);
        host.Backend.DeleteCount.ShouldBe(0);
    }

    [Fact]
    public async Task UnknownUuid_404_AfterOneLookup()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.GetAsync($"/v1/renders/{Guid.CreateVersion7()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.TaskUnknown);
        host.Store.TryGetCount.ShouldBe(1);
    }
}
