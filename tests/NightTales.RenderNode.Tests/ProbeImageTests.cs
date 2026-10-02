using System.Net;
using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// <c>POST /v1/probe-image</c> of 12 CR-19: the same auth path as every other route, a probe-only answer that never
/// reserves an image-relay slot and never submits a render, the closed reason enum on the wire, and
/// <c>503 adapter_probe_unavailable</c> while no probe backend is registered (what 12.8.3-T3 fails closed on).
/// </summary>
public sealed class ProbeImageTests
{
    private const string ProbePath = RenderNodeRoutes.ProbeImage;

    [Fact]
    public async Task WithoutKey_401()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.PostAsync(ProbePath, "{}", apiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.Unauthorized);
        host.Probe.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Funded_200_WithoutTouchingAPlanOrTheRenderBackend()
    {
        await using var host = await RenderNodeTestHost.StartAsync();

        var response = await host.PostAsync(ProbePath, "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.ShouldContain("\"funded\":true");
        var result = await RenderNodeTestHost.ReadAsync<ProbeImageResultV1>(response);
        result.Funded.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
        host.Probe.CallCount.ShouldBe(1);
        // A probe is excluded from capacity accounting and reserves no relay slot (12 CR-19, §10 T67).
        host.PlanSink.RegisterCount.ShouldBe(0);
        host.Backend.SubmitCount.ShouldBe(0);
        host.Store.InsertCount.ShouldBe(0);
    }

    [Fact]
    public async Task Unfunded_CarriesTheClosedReasonOnTheWire()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Probe.Result = new ProbeImageResultV1(null, false, "project_unfunded");

        var response = await host.PostAsync(ProbePath, "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.ShouldContain("\"reason\":\"project_unfunded\"");
        // The frozen §2 body is {"funded": …} / {"funded": false, "reason": …}: no schema literal exists yet.
        raw.ShouldNotContain("schema");
        // Never raw provider text (§1 envelope rule).
        raw.ShouldNotContain("openai");
    }

    [Fact]
    public async Task WithoutBackend_503_ProbeUnavailable()
    {
        await using var host = await RenderNodeTestHost.StartAsync(withProbeBackend: false);

        var response = await host.PostAsync(ProbePath, "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.ProbeUnavailable);
    }

    [Fact]
    public async Task Throttled_429_WithRetryAfter()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        host.Probe.Result = Error.Conflict(AdapterErrorCodes.ProbeThrottled);

        var response = await host.PostAsync(ProbePath, "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.TryGetValues(RenderNodeHeaders.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter!.Single().ShouldBe("60");
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.ProbeThrottled);
    }
}
