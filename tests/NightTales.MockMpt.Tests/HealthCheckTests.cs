using NightTales.MockMpt.Health;
using NightTales.MockMpt.Tests.TestHost;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The <c>--healthcheck</c> container probe (11.1.2-T1 review fixes CODE-1/CODE-2): it authenticates with the
/// same key resolution the server uses, so it needs no <c>curl</c> in the runtime image and a custom
/// <c>MOCK_API_KEY</c> is probed with the real value.
/// </summary>
public sealed class HealthCheckTests
{
    private static Func<string, string?> Reader(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void HealthCheck_IsSelectedByTheFlagOnly()
    {
        MockHealthCheck.IsRequested(["NightTales.MockMpt.dll"]).ShouldBeFalse();
        MockHealthCheck.IsRequested([MockHealthCheck.Flag]).ShouldBeTrue();
    }

    [Fact]
    public async Task HealthCheck_ProbesWithTheKeyTheServerBound()
    {
        await using var host = await MockMptTestHost.StartAsync();
        var url = new Uri(host.Client.BaseAddress!, "v1/health").ToString();

        // The container sets RenderNodeServer__ApiKey; the probe reads it, not a duplicated MOCK_API_KEY.
        var ok = await MockHealthCheck.RunAsync(
            Reader(("MOCK_HEALTH_URL", url), ("RenderNodeServer__ApiKey", MockMptTestHost.ApiKey)),
            TestContext.Current.CancellationToken);
        ok.ShouldBe(0);

        var wrongKey = await MockHealthCheck.RunAsync(
            Reader(("MOCK_HEALTH_URL", url), ("RenderNodeServer__ApiKey", "ffffffffffffffffffffffffffffffff")),
            TestContext.Current.CancellationToken);
        wrongKey.ShouldBe(1);
    }

    [Fact]
    public async Task HealthCheck_WithoutAUsableKey_IsOne()
    {
        var exit = await MockHealthCheck.RunAsync(
            Reader(("MOCK_API_KEY", "too-short"), ("MOCK_HEALTH_URL", "http://127.0.0.1:1/v1/health")),
            TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
    }
}
