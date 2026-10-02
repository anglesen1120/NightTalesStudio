using System.Net;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fakes;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// "Eight routes, nothing else" (05.4.1-T2 §2): every other path — including one with a file extension, which the
/// built-in <c>MapFallback</c> would let fall through to a bare 404 — is the
/// <c>404 adapter_route_not_found</c> envelope after a valid key, and 401 without one (§10 T68).
/// </summary>
public sealed class UnknownRouteTests
{
    [Theory]
    [InlineData("/v1/nope")]
    [InlineData("/nope.json")]
    [InlineData("/")]
    [InlineData("/v2/renders")]
    [InlineData("/v1/renders/")]
    public async Task Fallback_404(string path)
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.RouteNotFound);
    }

    [Fact]
    public async Task WrongMethodOnAKnownPath_404()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.PostAsync(RenderNodeRoutes.Health, "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.RouteNotFound);
    }

    [Fact]
    public async Task UnknownPath_WithoutKey_401Not404()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.GetAsync("/v1/nope", apiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized);
    }
}
