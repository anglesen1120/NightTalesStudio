using System.Net;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The core's <c>x-api-key</c> auth on every route (11.1.2-T1 Tests, 05 D15 S3): the mock adds no auth code,
/// so these pin the core's behaviour through the mock's host.
/// </summary>
public sealed class AuthTests
{
    private static readonly Guid Task = Guid.CreateVersion7();

    /// <summary>Every route with a method that reaches auth (the body is irrelevant: auth runs first).</summary>
    public static TheoryData<string, string> Routes => new()
    {
        { "POST", RenderNodeRoutes.Renders },
        { "GET", $"/v1/renders/{Task}" },
        { "GET", $"/v1/renders/{Task}/video" },
        { "GET", $"/v1/renders/{Task}/subtitles" },
        { "POST", $"/v1/renders/{Task}/cancel" },
        { "DELETE", $"/v1/renders/{Task}" },
        { "GET", RenderNodeRoutes.Health },
        { "POST", RenderNodeRoutes.ProbeImage },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Auth_MissingKey_401(string method, string path)
    {
        await using var host = await MockMptTestHost.StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), path, content: null, apiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Auth_WrongKey_401(string method, string path)
    {
        await using var host = await MockMptTestHost.StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), path, content: null, apiKey: "not-the-key");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Auth_RightKey_NotUnauthorized(string method, string path)
    {
        await using var host = await MockMptTestHost.StartAsync();

        using var response = await host.SendAsync(new HttpMethod(method), path, MockMptTestHost.Json("{}"));

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_RightKey_200()
    {
        await using var host = await MockMptTestHost.StartAsync();

        using var response = await host.GetAsync(RenderNodeRoutes.Health);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var health = await MockMptTestHost.ReadAsync<RenderNodeHealthV1>(response);
        health.Status.ShouldBe(RenderNodeHealthStatuses.Ok);
        health.Schema.ShouldBe(RenderNodeSchemas.RenderNodeHealth);
    }
}
