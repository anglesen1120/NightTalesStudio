using System.Net;
using System.Net.Http.Json;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.RateLimiting;

/// <summary>
/// The forwarded-header trust of 09.3.5-T1 (09 README BD14): an <c>X-Forwarded-For</c> counts only when
/// the peer that sent it is a configured proxy, exactly one hop is believed, and <c>X-Forwarded-Host</c>
/// is never believed at all.
/// </summary>
/// <remarks>
/// The probe route reports what the host resolved for the connection, which is what the limiter
/// partitions on — asserting the middleware's configuration would prove nothing about the address the
/// limiter ends up using. Every request pins a peer address, because the in-process <c>TestServer</c>
/// has no connection feature and would otherwise look like the address-less caller the resolver answers
/// <c>UnknownPeer</c> for.
/// </remarks>
public sealed class ForwardedHeadersTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The peer outside every configured network: its forwarded headers must be ignored.</summary>
    private const string UnknownPeer = "203.0.113.7";

    /// <summary>The CIDR a test declares as its load balancer; <see cref="UnknownPeer"/> is inside it.</summary>
    private const string TrustedNetwork = "203.0.113.0/24";

    /// <summary>The address a trusted proxy declares: the rightmost entry of <c>X-Forwarded-For</c>.</summary>
    private const string DeclaredClient = "9.9.9.9";

    /// <summary>An entry the caller itself prepended; believing it would let any caller choose its bucket.</summary>
    private const string AttackerSupplied = "1.2.3.4";

    private const string ForwardedFor = "X-Forwarded-For";

    private const string ForwardedProto = "X-Forwarded-Proto";

    private const string ForwardedHost = "X-Forwarded-Host";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HeaderFromUnknownPeer_IsIgnored()
    {
        // The shipped configuration trusts nothing outside Development, and Development trusts only the
        // loopback: 203.0.113.7 is inside neither list.
        using var factory = new ApiFactory(Database, new FakeClock()).MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(UnknownPeer);

        var probe = await ProbeAsync(client, (ForwardedFor, DeclaredClient));

        probe.RemoteIp.ShouldBe(
            UnknownPeer,
            "a peer outside the trusted networks cannot declare an address, so the spoofed IP gets no bucket");
    }

    [Fact]
    public async Task HeaderFromKnownNetwork_IsApplied()
    {
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{TrustSection}:{TrustNetworkKey}", TrustedNetwork)
            .MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(UnknownPeer);

        var probe = await ProbeAsync(
            client,
            (ForwardedFor, $"{AttackerSupplied}, {DeclaredClient}"),
            (ForwardedProto, "https"));

        probe.RemoteIp.ShouldBe(
            DeclaredClient,
            "ForwardLimit = 1 believes the one hop the trusted proxy declared, never an entry the caller prepended");
        probe.Scheme.ShouldBe("https", "the declared scheme is trusted with the same single hop");
    }

    [Fact]
    public async Task XForwardedHost_IsNeverTrusted()
    {
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{TrustSection}:{TrustNetworkKey}", TrustedNetwork)
            .MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(UnknownPeer);

        var baseline = await ProbeAsync(client);
        var spoofed = await ProbeAsync(client, (ForwardedHost, "evil.example.test"));

        spoofed.Host.ShouldBe(
            baseline.Host,
            "host-header injection: XForwardedHost is not among the trusted headers, whatever the peer");
    }

    [Fact]
    public async Task ForwardedFor_FromUnknownPeer_NeverMintsAPartition()
    {
        // The acceptance check behind the trust boundary: a caller that is not a configured proxy cannot
        // choose the bucket it is limited in. Ten login attempts from one address, each claiming a
        // different client, still exhaust one `auth` window — a believed claim would give every attempt a
        // bucket of its own, and none of the eleven would ever be refused.
        using var factory = new ApiFactory(Database, new FakeClock());
        using var client = factory.PeerClient(UnknownPeer);
        var email = $"spoof-{Guid.CreateVersion7():N}@nighttales.test";

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var admitted = await LoginClaimingAsync(client, email, $"9.9.9.{attempt}");

            admitted.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                $"attempt {attempt} is inside the peer's ten-per-minute window");
        }

        using var rejected = await LoginClaimingAsync(client, email, "9.9.9.99");

        rejected.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the eleven attempts share the peer's own partition: no claimed address became a bucket");
    }

    /// <summary>The configuration section the trust boundary reads.</summary>
    private const string TrustSection = "Http:ForwardedHeaders";

    /// <summary>The key holding the comma-separated CIDR list of the peers whose headers are believed.</summary>
    private const string TrustNetworkKey = "KnownNetworks";

    /// <summary>Logs in once, claiming <paramref name="forwardedFor"/> as the client's address.</summary>
    private static async Task<HttpResponseMessage> LoginClaimingAsync(
        HttpClient client,
        string email,
        string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthTestFlow.LoginPath)
        {
            Content = JsonContent.Create(new { email, password = AuthTestFlow.Password }),
        };
        request.Headers.Add(ForwardedFor, forwardedFor);

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Reads the probe's answer for one request carrying <paramref name="headers"/>.</summary>
    private static async Task<ProbeEndpoints.RemoteIpProbe> ProbeAsync(
        HttpClient client,
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProbeEndpoints.RemoteIpPath);

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var probe = await response.Content.ReadFromJsonAsync<ProbeEndpoints.RemoteIpProbe>(Ct);
        probe.ShouldNotBeNull();

        return probe;
    }
}
