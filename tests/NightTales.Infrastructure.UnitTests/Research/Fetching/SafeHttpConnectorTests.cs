using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research.Http;
using NightTales.TestKit.Fetching;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>
/// The anti-rebinding core (02 D11, F14). The control is resolve-then-connect-to-IP: the connect callback resolves the name,
/// validates every answer and dials the validated <see cref="IPAddress"/>, and <see cref="HttpClient"/> never resolves
/// again. The server below listens on loopback; what the resolver answers decides whether it is reached.
/// </summary>
public sealed class SafeHttpConnectorTests
{
    private static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");

    private static HttpTestServer Server() => new(_ => TestResponse.Text(200, "text/plain", "hello"));

    private static HttpClient Client(
        HttpTestServer server,
        FakeDnsResolver dns,
        IAddressValidator validator) =>
        SafeHttpClientFactory.Create(new SafeHttpConnector(
            dns,
            validator,
            NullLogger<SafeHttpConnector>.Instance,
            server.EndpointFor(HttpTestServer.ClosedPort())));

    private static async Task<SourceFetchException> ConnectFailureAsync(HttpClient client, string url)
    {
        var thrown = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(url, TestContext.Current.CancellationToken));

        return SafeHttp.FindFetchException(thrown).ShouldNotBeNull();
    }

    [Fact]
    public async Task Connect_ResolverReturnsPublicWhileServerListensOnLoopback_ServerSeesNoConnection()
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("rebind.test", Public);
        using var client = Client(server, dns, new LoopbackAllowingAddressValidator());

        var failure = await ConnectFailureAsync(client, "http://rebind.test/");

        failure.Error.Code.ShouldBe(SourceErrors.SourceUnreachableCode);
        server.Connections.ShouldBe(0);
        server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connect_ResolverReturnsLoopback_ServerSeesExactlyOneRequest()
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("rebind.test", IPAddress.Loopback);
        using var client = Client(server, dns, new LoopbackAllowingAddressValidator());

        using var response = await client.GetAsync("http://rebind.test/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        server.Requests.Count.ShouldBe(1);
        dns.Lookups.ShouldBe(["rebind.test"], "the name is resolved once, by the connect callback, and never again");
    }

    [Fact]
    public async Task Connect_FirstAllowedAddressRefused_SecondSucceeds_UsesNewSocket()
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("two.test", Public, IPAddress.Loopback);
        using var client = Client(server, dns, new LoopbackAllowingAddressValidator());

        using var response = await client.GetAsync("http://two.test/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Connect_ResolverReturnsPublicThenLoopback_ConnectsToPublicOnly()
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("mixed.test", Public, IPAddress.Loopback);
        using var client = Client(server, dns, new GlobalUnicastAddressValidator());

        var failure = await ConnectFailureAsync(client, "http://mixed.test/");

        failure.Error.Code.ShouldBe(SourceErrors.SourceUnreachableCode);
        server.Connections.ShouldBe(0, "no fall-back to the loopback answer");
    }

    [Fact]
    public async Task Connect_OnlyBlockedAddresses_FailsClosed()
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("blocked.test", IPAddress.Loopback, IPAddress.Parse("10.0.0.1"));
        using var client = Client(server, dns, new GlobalUnicastAddressValidator());

        var failure = await ConnectFailureAsync(client, "http://blocked.test/");

        failure.Error.Code.ShouldBe(SourceErrors.SourceUnreachableCode);
        server.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task Connect_NoDnsRecords_IsUnreachable()
    {
        await using var server = Server();
        using var client = Client(server, new FakeDnsResolver(), new LoopbackAllowingAddressValidator());

        var failure = await ConnectFailureAsync(client, "http://nothing.test/");

        failure.Error.Code.ShouldBe(SourceErrors.SourceUnreachableCode);
    }

    [Theory]
    [InlineData("http://rebind.test:8080/")]
    [InlineData("http://127.0.0.1/")]
    public async Task Connect_PortOutside80And443_OrIpLiteral_IsUrlInvalid(string url)
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("rebind.test", IPAddress.Loopback);
        using var client = Client(server, dns, new LoopbackAllowingAddressValidator());

        var failure = await ConnectFailureAsync(client, url);

        failure.Error.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
        server.Connections.ShouldBe(0);
    }

    /// <summary>
    /// Defence in depth only — NOT the rebinding control (that is resolve-then-connect-to-IP, asserted above): the peer
    /// re-check fires if the socket API ever returned a peer other than the endpoint asked for. The scripted validator
    /// allows the first call (the candidate address) and denies the second (the connected peer).
    /// </summary>
    [Fact]
    public async Task Connect_PeerAddressDeniedByScriptedValidator_Throws()
    {
        await using var server = Server();
        var dns = new FakeDnsResolver().Map("peer.test", IPAddress.Loopback);
        using var client = Client(server, dns, new AllowFirstCallOnlyValidator());

        var failure = await ConnectFailureAsync(client, "http://peer.test/");

        failure.Error.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
    }

    private sealed class AllowFirstCallOnlyValidator : IAddressValidator
    {
        private int _calls;

        public bool IsAllowed(IPAddress address, out string? reason)
        {
            reason = null;

            return Interlocked.Increment(ref _calls) == 1;
        }
    }
}
