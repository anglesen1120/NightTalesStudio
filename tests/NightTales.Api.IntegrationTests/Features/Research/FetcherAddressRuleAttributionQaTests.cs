using System.Net;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.TestKit.Fetching;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// QA addition from the 02.5.2-T4 review (not part of the suite's 37-case map): proves that a host name resolving to a
/// private address is blocked by <see cref="GlobalUnicastAddressValidator"/> and not merely by a refused connection.
/// </summary>
/// <remarks>
/// The suite's own host-name cases cannot attribute the block. <see cref="HttpTestServer.EndpointFor"/> dials every
/// non-loopback validated address at a closed port, so those cases fail identically whether the address rule fired or the
/// connection was refused — deleting the <c>10.0.0.0/8</c> row from <see cref="GlobalUnicastAddressValidator.BlockedIpv4"/>
/// leaves the whole filtered run green (verified by the review). Here the connector dials the test server for <em>every</em>
/// validated address, so the address rule is the only thing that can keep the server at zero connections; a permissive
/// validator then proves with a positive control that the same request does reach the server.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FetcherAddressRuleAttributionQaTests
{
    /// <summary>Every private address the suite's host-name cases and the F14 IPv4 gaps resolve to.</summary>
    /// <param name="address">The address the scripted resolver answers.</param>
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("240.0.0.1")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    public async Task HostNameResolvingToPrivateAddress_IsBlockedByTheAddressRule_NotByARefusedConnection(string address)
    {
        const string Url = "http://private-attribution-e2e.com/ok";
        await using var server = new HttpTestServer(HttpTestRoutes.Standard);

        // The seam dials the test server for every validated address, so a refused connection can never be the cause.
        var endpointFor = (IPAddress _, int _) => new IPEndPoint(IPAddress.Loopback, server.Port);
        using var loggers = LoggerFactory.Create(_ => { });

        var strict = new SafeHttpConnector(
            Resolver(address), new GlobalUnicastAddressValidator(), loggers.CreateLogger<SafeHttpConnector>(), endpointFor);
        using (var client = SafeHttpClientFactory.Create(strict))
        {
            await Should.ThrowAsync<Exception>(() => client.GetAsync(Url, Ct));
        }

        server.Connections.ShouldBe(0, $"{address}: the address rule must block before any connect");
        server.Requests.ShouldBeEmpty($"{address}: the server must see no request at all");

        var permissive = new SafeHttpConnector(
            Resolver(address), new AllowEveryAddressValidator(), loggers.CreateLogger<SafeHttpConnector>(), endpointFor);
        using (var client = SafeHttpClientFactory.Create(permissive))
        using (var response = await client.GetAsync(Url, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, "the positive control: an allowed address reaches the server");
        }

        server.Connections.ShouldBeGreaterThan(0, $"{address}: the positive control proves the test server is reachable");
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
}
