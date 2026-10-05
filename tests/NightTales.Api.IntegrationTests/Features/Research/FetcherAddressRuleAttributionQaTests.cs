using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.TestKit.Fetching;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// QA addition from the 02.5.2-T4 review (not part of the suite's 37-case map): proves that a host name resolving to a
/// private address — IPv4 or IPv6 — is blocked by <see cref="GlobalUnicastAddressValidator"/> and not merely by a refused
/// connection.
/// </summary>
/// <remarks>
/// The suite's own host-name cases cannot attribute the block. <see cref="HttpTestServer.EndpointFor"/> dials every
/// non-loopback validated address at a closed port, so those cases fail identically whether the address rule fired or the
/// connection was refused — deleting the <c>10.0.0.0/8</c> row from <see cref="GlobalUnicastAddressValidator.BlockedIpv4"/>
/// leaves the whole filtered run green (verified by the review). Here the connector dials a reachable loopback endpoint of
/// the address's own family for <em>every</em> validated address, so the address rule is the only thing that can keep the
/// servers at zero connections; a permissive validator then proves with a positive control that the same request does reach
/// a loopback server.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FetcherAddressRuleAttributionQaTests
{
    /// <summary>One address per <see cref="GlobalUnicastAddressValidator"/> block row (and its 2000::/3 gate), plus the addresses the suite's own host-name cases resolve to.</summary>
    /// <param name="address">The address the scripted resolver answers.</param>
    [Theory]
    // IPv4: one address per BlockedIpv4 row.
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    // IPv6: one address per BlockedIpv6 row (including the normalisation and non-2000::/3 paths).
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::2")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("100:0:0:1::1")]
    [InlineData("2001::1")]
    [InlineData("2001:1::1")]
    [InlineData("2001:1::2")]
    [InlineData("2001:2::1")]
    [InlineData("2001:3::1")]
    [InlineData("2001:4:112::1")]
    [InlineData("2001:20::1")]
    [InlineData("2001:30::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("2620:4f:8000::1")]
    [InlineData("3fff::1")]
    [InlineData("5f00::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    // An IPv6 address outside 2000::/3 that is in no block row: the positive global-unicast gate, not the table.
    [InlineData("4000::1")]
    public async Task HostNameResolvingToPrivateAddress_IsBlockedByTheAddressRule_NotByARefusedConnection(string address)
    {
        const string Url = "http://private-attribution-e2e.com/ok";
        var isV6 = IPAddress.Parse(address).AddressFamily == AddressFamily.InterNetworkV6;
        if (isV6 && !Socket.OSSupportsIPv6)
        {
            Assert.Skip($"{address}: this host has no IPv6 loopback, so the IPv6 attribution row cannot run");
        }

        await using var server = new HttpTestServer(HttpTestRoutes.Standard);
        // Only IPv6 rows bind ::1, so the IPv4 rows never depend on the host's IPv6 support.
        await using var v6 = isV6 ? new Ipv6LoopbackServer() : null;

        // The seam dials a reachable loopback endpoint of the address's own family for every validated address, so a
        // refused connection can never be the cause of the block (a v6 address can never reach the IPv4-only HttpTestServer).
        var endpointFor = (IPAddress dialed, int _) => dialed.AddressFamily == AddressFamily.InterNetworkV6 && v6 is not null
            ? new IPEndPoint(IPAddress.IPv6Loopback, v6.Port)
            : new IPEndPoint(IPAddress.Loopback, server.Port);
        using var loggers = LoggerFactory.Create(_ => { });

        var strict = new SafeHttpConnector(
            Resolver(address), new GlobalUnicastAddressValidator(), loggers.CreateLogger<SafeHttpConnector>(), endpointFor);
        using (var client = SafeHttpClientFactory.Create(strict))
        {
            await Should.ThrowAsync<Exception>(() => client.GetAsync(Url, Ct));
        }

        server.Connections.ShouldBe(0, $"{address}: the address rule must block before any connect");
        server.Requests.ShouldBeEmpty($"{address}: the server must see no request at all");
        (v6?.Connections ?? 0).ShouldBe(0, $"{address}: the IPv6 loopback endpoint must see no connection either");

        var permissive = new SafeHttpConnector(
            Resolver(address), new AllowEveryAddressValidator(), loggers.CreateLogger<SafeHttpConnector>(), endpointFor);
        using (var client = SafeHttpClientFactory.Create(permissive))
        using (var response = await client.GetAsync(Url, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, "the positive control: an allowed address reaches a loopback server");
        }

        if (isV6)
        {
            v6!.Connections.ShouldBeGreaterThan(0, $"{address}: the positive control proves the IPv6 loopback endpoint is reachable");
            server.Connections.ShouldBe(0, $"{address}: a v6 address never touches the IPv4 server");
        }
        else
        {
            server.Connections.ShouldBeGreaterThan(0, $"{address}: the positive control proves the test server is reachable");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeDnsResolver Resolver(string address) =>
        new FakeDnsResolver().Map("private-attribution-e2e.com", IPAddress.Parse(address));

    /// <summary>Allows every address, so the positive control isolates the address rule as the only difference.</summary>
    private sealed class AllowEveryAddressValidator : IAddressValidator
    {
        /// <inheritdoc />
        public bool IsAllowed(IPAddress address, out string? reason)
        {
            reason = null;

            return true;
        }
    }

    /// <summary>
    /// A minimal IPv6-loopback HTTP/1.1 server for the IPv6 rows of the theory. <see cref="HttpTestServer"/> listens on
    /// IPv4 only, so an IPv6 socket can never reach it; this listener proves, in the permissive control, that the connector
    /// really dials a validated IPv6 address (rather than the block coming from a family mismatch).
    /// </summary>
    private sealed class Ipv6LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.IPv6Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _connections;

        public Ipv6LoopbackServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync();
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            await _accepting.ConfigureAwait(false);
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _connections);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopping.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096], _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes("HTTP/1.1 200 X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // The client went away.
                }
            }
        }
    }
}
