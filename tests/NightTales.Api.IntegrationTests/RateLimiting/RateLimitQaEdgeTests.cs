using System.Net;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using NightTales.Api.IntegrationTests.Auth;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.RateLimiting;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.RateLimiting;

/// <summary>
/// QA edge coverage for 09.3.5-T1 on top of <see cref="RateLimitTests"/> (09 README BD14/BD16): the
/// boundary of the shipped limit rather than its inside, the window reset of a short window, the
/// address families the same client can arrive under (IPv6, IPv4-mapped-IPv6), a malformed
/// <c>X-Forwarded-For</c> value, and the health probe's pass-through.
/// </summary>
public sealed class RateLimitQaEdgeTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>An IPv4 peer outside every configured network.</summary>
    private const string PeerV4 = "203.0.113.40";

    /// <summary>The same client as an IPv4-mapped IPv6 peer (BD16: one client, one bucket).</summary>
    private const string PeerMapped = "::ffff:203.0.113.40";

    /// <summary>An IPv6 peer, for the family-agnostic partition assertion.</summary>
    private const string PeerV6 = "2001:db8::7";

    /// <summary>One address of an IPv6 /64 (SEC-0935-02: an IPv6 client controls the whole prefix).</summary>
    private const string PeerV6PrefixA = "2001:db8:1:2::1";

    /// <summary>A second address of the same /64 as <see cref="PeerV6PrefixA"/>.</summary>
    private const string PeerV6PrefixB = "2001:db8:1:2::beef";

    /// <summary>An address of a different /64, one that must stay isolated.</summary>
    private const string PeerV6OtherPrefix = "2001:db8:1:3::1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FixedWindow_ResetsAfterTheWindow()
    {
        // A one-second one-permit window: two requests in the window refuse the second, and a request
        // after the window rolls is admitted again — the reset, not merely the wall.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:PermitLimit", "1")
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:WindowSeconds", "1")
            .MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(PeerV4);

        using var first = await client.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var insideWindow = await client.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        insideWindow.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "a second request inside the window is refused");
        insideWindow.Headers.RetryAfter.ShouldNotBeNull();
        insideWindow.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(
            0, "the advertised wait is never zero or negative (09.3.5-T1 design)");

        await Task.Delay(TimeSpan.FromMilliseconds(1400), Ct);

        using var afterWindow = await client.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        afterWindow.StatusCode.ShouldBe(HttpStatusCode.OK, "the fixed window reset and admits again");
    }

    [Fact]
    public async Task HubPolicy_ExactLimitTen_IsNotRejected()
    {
        // The boundary of the limit, not its outside: exactly HubPermits requests of one user are all
        // admitted, so an off-by-one in the policy would stand out.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Hub)}:PermitLimit", "2")
            .MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(PeerV4, Guid.CreateVersion7());

        using var first = await client.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var atLimit = await client.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        atLimit.StatusCode.ShouldBe(HttpStatusCode.OK, "a request exactly at the limit is admitted");

        using var over = await client.PostAsync(ProbeEndpoints.HubRateLimitPath, null, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "only limit+1 is refused");
    }

    [Fact]
    public async Task AnonPolicy_Ipv6Peer_HasItsOwnBucket()
    {
        // The IP-keyed policies partition on an address regardless of family: an IPv6 peer is limited
        // by the same policy and shares its bucket with no IPv4 peer.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:PermitLimit", "1")
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:WindowSeconds", "1")
            .MapEndpoints(ProbeEndpoints.Map);
        using var v6 = factory.PeerClient(PeerV6);
        using var v4 = factory.PeerClient(PeerV4);

        using var first = await v6.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var over = await v6.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the IPv6 peer's second request is refused");

        using var stranger = await v4.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        stranger.StatusCode.ShouldBe(HttpStatusCode.OK, "an IPv4 peer has a bucket of its own");
    }

    [Fact]
    public async Task AnonPolicy_Ipv4MappedIpv6Peer_SharesTheIpv4Bucket()
    {
        // The same client arriving as IPv4 and as IPv4-mapped-IPv6 is one caller: the limiter
        // partitioning on the address must see one bucket, or the mapped form hands the client a
        // second, untouched budget.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:PermitLimit", "2")
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:WindowSeconds", "1")
            .MapEndpoints(ProbeEndpoints.Map);
        using var v4 = factory.PeerClient(PeerV4);
        using var mapped = factory.PeerClient(PeerMapped);

        using var a = await v4.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        a.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var b = await mapped.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        b.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "the second request inside the limit is admitted whichever address family the client uses");

        using var c = await v4.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        c.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the mapped form spent the same bucket: a client cannot double its limit by alternating address families");
    }

    [Fact]
    public void PartitionKey_MappedIpv4AndNativeIpv6_HashAsExpected()
    {
        // The unit-level view of the family assertions above: the key is the truncated SHA-256 of the
        // address bytes, so two representations of one client must collapse to one key — and one IPv6
        // prefix must be one bucket however many interface addresses the subscriber rotates inside it.
        const int prefix = RateLimitingOptions.DefaultIpv6PrefixLength;
        var mapped = RateLimitPartitionKeys.Hash(IPAddress.Parse(PeerMapped), prefix);
        var v4 = RateLimitPartitionKeys.Hash(IPAddress.Parse(PeerV4), prefix);
        var v6 = RateLimitPartitionKeys.Hash(IPAddress.Parse(PeerV6), prefix);

        mapped.ShouldBe(v4, "an IPv4-mapped IPv6 peer and the IPv4 peer are the same client");
        v6.ShouldNotBe(v4, "a different address is a different bucket");

        var samePrefix = RateLimitPartitionKeys.Hash(IPAddress.Parse(PeerV6PrefixA), prefix);
        var samePrefixSibling = RateLimitPartitionKeys.Hash(IPAddress.Parse(PeerV6PrefixB), prefix);
        var otherPrefix = RateLimitPartitionKeys.Hash(IPAddress.Parse(PeerV6OtherPrefix), prefix);

        samePrefix.ShouldBe(samePrefixSibling, "two addresses of one /64 are one partition (SEC-0935-02)");
        samePrefix.ShouldNotBe(otherPrefix, "a different /64 is a different partition");
    }

    [Fact]
    public async Task AnonPolicy_Ipv6AddressesInOneSlash64_ShareOneBucket()
    {
        // The abuse fix behind SEC-0935-02: an IPv6 subscriber controls its whole /64, so rotating the
        // interface address inside that prefix must not buy a fresh budget. Two addresses of one /64
        // spend one window; a different /64 stays isolated.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:PermitLimit", "2")
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:WindowSeconds", "60")
            .MapEndpoints(ProbeEndpoints.Map);
        using var first = factory.PeerClient(PeerV6PrefixA);
        using var sibling = factory.PeerClient(PeerV6PrefixB);
        using var other = factory.PeerClient(PeerV6OtherPrefix);

        using var acceptedFirst = await first.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        acceptedFirst.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var acceptedSibling = await sibling.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        acceptedSibling.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "the second address of the same /64 is inside the shared two-permit window");

        using var over = await first.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        over.StatusCode.ShouldBe(
            HttpStatusCode.TooManyRequests,
            "the two addresses spent one /64 budget, so a third request is refused");

        using var stranger = await other.PostAsync(ProbeEndpoints.AnonRateLimitPath, null, Ct);
        stranger.StatusCode.ShouldBe(HttpStatusCode.OK, "a different /64 has its own budget");
    }

    [Fact]
    public async Task MalformedXForwardedFor_IsIgnored()
    {
        // A known proxy that forwards garbage must leave the resolved address alone: a malformed entry
        // is no address at all, so the peer keeps its own bucket.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting("Http:ForwardedHeaders:KnownNetworks", "203.0.113.0/24")
            .MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(PeerV4);

        using var request = new HttpRequestMessage(HttpMethod.Get, ProbeEndpoints.RemoteIpPath);
        request.Headers.Add("X-Forwarded-For", "not-an-ip-address");
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var probe = await response.Content.ReadFromJsonAsync<ProbeEndpoints.RemoteIpProbe>(Ct);
        probe.ShouldNotBeNull();
        probe!.RemoteIp.ShouldBe(
            PeerV4,
            "a malformed X-Forwarded-For changes nothing — the peer's own address still partitions the request");
    }

    [Fact]
    public async Task HealthLive_IsNotRateLimited()
    {
        // BD16: a probe fleet shares one address, so a health route must carry no policy. With the
        // anonymous policy pinned to two permits, twenty probes from one peer are all admitted.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:PermitLimit", "2")
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Auth)}:PermitLimit", "2");
        using var client = factory.PeerClient(PeerV4);

        for (var probe = 1; probe <= 20; probe++)
        {
            using var response = await client.GetAsync("/health/live", Ct);
            response.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"probe {probe}: /health/live carries no policy, so a shared address can never exhaust it");
        }
    }

    [Fact]
    public void ZeroWindowSeconds_FailsStartup()
    {
        // The zero end of the window bound: `RateLimiting:Anon:WindowSeconds=0` must refuse the boot,
        // exactly as a zero permit limit does (the shipped validator, exercised through the real host).
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting($"{RateLimitingOptions.SectionName}:{nameof(RateLimitingOptions.Anon)}:WindowSeconds", "0");

        var exception = Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(
            () => factory.CreateClient());

        exception.Message.ShouldBe(RateLimitingOptions.TooShortWindowFailure);
    }

    [Fact]
    public async Task XForwardedFor_FromMappedIpv6Peer_InsideKnownNetwork()
    {
        // A dual-stack Kestrel reports an IPv4 peer as IPv4-mapped IPv6: if the trusted-network check
        // compares families literally, a proxy whose CIDR is written as IPv4 is never matched and its
        // declared client is silently ignored.
        using var factory = new ApiFactory(Database, new FakeClock())
            .WithSetting("Http:ForwardedHeaders:KnownNetworks", "203.0.113.0/24")
            .MapEndpoints(ProbeEndpoints.Map);
        using var client = factory.PeerClient(PeerMapped);

        using var request = new HttpRequestMessage(HttpMethod.Get, ProbeEndpoints.RemoteIpPath);
        request.Headers.Add("X-Forwarded-For", "9.9.9.9");
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var probe = await response.Content.ReadFromJsonAsync<ProbeEndpoints.RemoteIpProbe>(Ct);
        probe.ShouldNotBeNull();
        probe!.RemoteIp.ShouldBe(
            "9.9.9.9",
            "a peer in the trusted CIDR declares the client address, whichever family its socket reports");
    }
}
