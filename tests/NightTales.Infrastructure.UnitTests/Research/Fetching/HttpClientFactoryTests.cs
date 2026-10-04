using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Infrastructure.Research.Http;
using NightTales.TestKit.Fetching;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>Each handler setting of <see cref="SafeHttpClientFactory"/> is a security control: one named assertion each.</summary>
public sealed class HttpClientFactoryTests
{
    private static SocketsHttpHandler Handler() => SafeHttpClientFactory.CreateHandler(
        new SafeHttpConnector(new FakeDnsResolver(), new GlobalUnicastAddressValidator(), NullLogger<SafeHttpConnector>.Instance));

    [Fact]
    public void Handler_AllowAutoRedirectFalse() => Handler().AllowAutoRedirect.ShouldBeFalse();

    [Fact]
    public void Handler_UseProxyFalse() => Handler().UseProxy.ShouldBeFalse();

    [Fact]
    public void Handler_UseCookiesFalse() => Handler().UseCookies.ShouldBeFalse();

    [Fact]
    public void Handler_AutomaticDecompressionNone() => Handler().AutomaticDecompression.ShouldBe(DecompressionMethods.None);

    [Fact]
    public void Handler_ConnectsThroughTheCallback_WithBoundedTimeouts()
    {
        var handler = Handler();

        handler.ConnectCallback.ShouldNotBeNull();
        handler.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        handler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Client_TimeoutIsInfinite_TheBudgetIsACancellationTokenPerFetch()
    {
        using var client = SafeHttpClientFactory.Create(
            new SafeHttpConnector(new FakeDnsResolver(), new GlobalUnicastAddressValidator(), NullLogger<SafeHttpConnector>.Instance));

        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    // QUIC (HTTP/3) does not use ConnectCallback, so the address validator would be bypassed. Pinning the request to
    // 2.0 with RequestVersionOrLower keeps every connection on 1.1/2; this test makes the pin explicit.
    [Fact]
    public void CreateGet_PinsHttp2_OrLower_NeverHttp3()
    {
        using var request = SafeHttp.CreateGet(new Uri("https://example.com/"), "ua", SafeHttp.PageAccept, null);

        request.Version.ShouldBe(HttpVersion.Version20);
        request.VersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionOrLower);
    }
}
