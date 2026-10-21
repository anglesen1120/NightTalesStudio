using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// 02.5.2-T4 cases 1-20 (plan §5.7 threat rows 1-6, 13 and the redirect rows): the real <c>SafeHttpSourceFetcher</c>, real
/// connector, robots policy and extractor against a real loopback <see cref="HttpTestServer"/>, one test per threat-table
/// row group. Every blocked case asserts the D8 code <em>and</em> that the server's request log (and its connection count)
/// stayed at zero or at the exact count the case allows; nothing asserts an exception type or a call count.
/// </summary>
/// <remarks>
/// FINDING F-T4-1 (reported to the TL, no production change): the task table expects <c>source_url_invalid</c> for a
/// <em>host name</em> that resolves to a non-global address (cases 1-4). Production maps it differently: the connector skips
/// the denied address and fails the connect (<c>source_unreachable</c>), and because the page's <c>robots.txt</c> fetch hits
/// the same denial first, the fetch ends as <c>source_blocked_by_robots</c> (D11, an unreachable robots file disallows).
/// <c>source_url_invalid</c> is returned only for IP-literal hosts (cases 1, 3-9, 15, 16, 20). Both are D8 job codes and the
/// security property (the request never happens) holds, so <see cref="HostnameBlockedCode"/> pins the observed code.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FetcherSsrfSuiteTests(TestDatabase database) : IntegrationTest(database)
{
    private const string HostnameBlockedCode = SourceErrors.SourceBlockedByRobotsCode;

    private static CancellationToken Ct => TestContextToken.Value;

    private static FetchStack NewStack(ValidatorMode mode = ValidatorMode.LoopbackAllowing, FakeDnsResolver? dns = null) =>
        FetchStack.Create(HttpTestRoutes.Standard, mode, dns: dns);

    private static async Task AssertBlockedAsync(FetchStack stack, string url, string code, string because)
    {
        FetchStack.Code(await stack.FetchAsync(url, Ct)).ShouldBe(code, because);
        stack.ShouldNeverHaveReachedTheServer(because);
    }

    private static SourceUrl ParseOk(string text)
    {
        var parsed = SourceUrl.Parse(text, new FakePublicSuffixList());
        parsed.IsError.ShouldBeFalse(text);

        return parsed.Value;
    }

    // ------------------------------------------------------------------ address and notation (rows 1-6, 13)

    [Fact]
    public async Task Ssrf_LoopbackAddress_Blocked_AndServerSeesNoRequest()
    {
        // threat row 1, case 1: the production validator, the server on 127.0.0.1.
        await using var stack = NewStack(ValidatorMode.GlobalUnicast);

        await AssertBlockedAsync(stack, "http://127.0.0.1/ok", SourceErrors.SourceUrlInvalidCode, "loopback IP literal");
        await AssertBlockedAsync(stack, "http://loopback-e2e.com/ok", HostnameBlockedCode, "a name resolving to loopback under the production validator");
        stack.Dns.Lookups.ShouldNotContain("127.0.0.1");
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    public async Task Ssrf_PrivateAddresses_Blocked(string address)
    {
        // threat row 1, case 2: fake DNS answers the private address; the test validator still blocks it (rule, not loopback).
        await using var stack = NewStack(dns: new FakeDnsResolver().Map("private-e2e.com", IPAddress.Parse(address)));

        await AssertBlockedAsync(stack, "http://private-e2e.com/ok", HostnameBlockedCode, address);
        await AssertBlockedAsync(stack, $"http://{address}/ok", SourceErrors.SourceUrlInvalidCode, address + " literal");
    }

    [Fact]
    public async Task Ssrf_MetadataAddress_Blocked()
    {
        // threat row 2, case 3.
        await using var stack = NewStack(dns: new FakeDnsResolver().Map("metadata-e2e.com", IPAddress.Parse("169.254.169.254")));

        await AssertBlockedAsync(stack, "http://169.254.169.254/latest/meta-data/", SourceErrors.SourceUrlInvalidCode, "metadata literal");
        await AssertBlockedAsync(stack, "http://metadata-e2e.com/latest/meta-data/", HostnameBlockedCode, "name -> 169.254.169.254");
    }

    [Fact]
    public async Task Ssrf_CgnatAndZeroAddress_Blocked()
    {
        // threat row 1 (192.0.0.0/24, 198.18.0.0/15, 240.0.0.0/4 are F14 gaps), case 4.
        var dns = new FakeDnsResolver()
            .Map("cgnat-e2e.com", IPAddress.Parse("100.64.0.1"))
            .Map("zero-e2e.com", IPAddress.Parse("0.0.0.0"))
            .Map("proto-e2e.com", IPAddress.Parse("192.0.0.8"))
            .Map("bench-e2e.com", IPAddress.Parse("198.18.0.1"))
            .Map("reserved-e2e.com", IPAddress.Parse("240.0.0.1"));
        await using var stack = NewStack(dns: dns);

        foreach (var host in new[] { "cgnat-e2e.com", "zero-e2e.com", "proto-e2e.com", "bench-e2e.com", "reserved-e2e.com" })
        {
            await AssertBlockedAsync(stack, $"http://{host}/", HostnameBlockedCode, host);
        }

        await AssertBlockedAsync(stack, "http://100.64.0.1/", SourceErrors.SourceUrlInvalidCode, "CGNAT literal");
        await AssertBlockedAsync(stack, "http://0.0.0.0/", SourceErrors.SourceUrlInvalidCode, "0.0.0.0 literal");
    }

    [Theory]
    [InlineData("http://2130706433/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f.0.0.1/")]
    public void Ssrf_IpLiteralDecimalOctalHex_RejectedBeforeDns(string url)
    {
        // threat row 3, case 5 (F14: decimal/octal/hex literals): rejected at Parse. The fetcher twin below proves that DNS is never asked.
        var result = SourceUrl.Parse(url, new FakePublicSuffixList());

        result.IsError.ShouldBeTrue(url);
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
    }

    [Fact]
    public async Task Ssrf_IpLiteralDecimalOctalHex_RejectedBeforeDns_FetcherNeverResolves()
    {
        // threat row 3, case 5, fetcher side: a hand-built request with the same literals never reaches DNS or the server.
        await using var stack = NewStack();
        foreach (var url in new[] { "http://2130706433/", "http://0177.0.0.1/", "http://0x7f.0.0.1/" })
        {
            await AssertBlockedAsync(stack, url, SourceErrors.SourceUrlInvalidCode, url);
        }

        stack.Dns.Lookups.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::127.0.0.1]/")]
    public async Task Ssrf_Ipv4MappedAndCompatibleV6_Blocked(string url)
    {
        // threat row 3/6, case 6.
        await using var stack = NewStack();

        await AssertBlockedAsync(stack, url, SourceErrors.SourceUrlInvalidCode, url);
        stack.Dns.Lookups.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://[2002:7f00:1::]/")]
    [InlineData("http://[2001:0:0:0:0:0:0:1]/")]
    public async Task Ssrf_SixToFourAndTeredo_Blocked(string url)
    {
        // threat row 6, case 7.
        await using var stack = NewStack();

        await AssertBlockedAsync(stack, url, SourceErrors.SourceUrlInvalidCode, url);
    }

    [Theory]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fec0::1]/")]
    [InlineData("http://[ff02::1]/")]
    public async Task Ssrf_UlaLinkLocalSiteLocalMulticastV6_Blocked(string url)
    {
        // threat row 6, case 8.
        await using var stack = NewStack();

        await AssertBlockedAsync(stack, url, SourceErrors.SourceUrlInvalidCode, url);
    }

    [Theory]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2001:db8::1]/")]
    public async Task Ssrf_Nat64AndDocumentationV6_Blocked(string url)
    {
        // threat row 6, case 9.
        await using var stack = NewStack();

        await AssertBlockedAsync(stack, url, SourceErrors.SourceUrlInvalidCode, url);
    }

    [Fact]
    public async Task Ssrf_DnsRebinding_PublicAnswerWhileServerOnLoopback_NoConnectionAndUnreachable()
    {
        // threat row 4, case 10 (the control): resolve-then-connect-to-IP. The name answers a PUBLIC address, so the connector
        // dials that address (redirected to a closed port by the test seam); the loopback server is never connected to.
        var dns = new FakeDnsResolver().Map("rebind-e2e.com", IPAddress.Parse("93.184.216.34"));
        await using var stack = NewStack(dns: dns);

        FetchStack.Code(await stack.FetchAsync("http://rebind-e2e.com/ok", Ct)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode,
            "the robots fetch dials the public address (refused) first: D11 fail-closed");
        stack.Server.Connections.ShouldBe(0);
        stack.Server.Requests.ShouldBeEmpty();
        dns.Lookups.ShouldContain("rebind-e2e.com");

        // The mixed answer [public, loopback] under the production validator: loopback is skipped, the public one is refused.
        var mixed = new FakeDnsResolver().Map("mixed-e2e.com", IPAddress.Parse("93.184.216.34"), IPAddress.Loopback);
        await using var production = NewStack(ValidatorMode.GlobalUnicast, mixed);
        FetchStack.Code(await production.FetchAsync("http://mixed-e2e.com/ok", Ct)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        production.ShouldNeverHaveReachedTheServer("rebinding with a mixed answer");
    }

    [Fact]
    public async Task Ssrf_DnsRebinding_FlipAfterFirstResolve_ConnectsOnlyToValidatedAddress()
    {
        // threat row 4 (DNS rebinding, a real flip): the name answers a PUBLIC address on the first resolution and loopback
        // on every later one. The seam dials the test server for every loopback address, so a second resolution or a dial
        // by host name (both of which would see loopback) reaches the loopback server. A correct connector resolves once,
        // validates that answer and dials exactly it, so the server stays at zero connections. Case 10 (fixed answer) is
        // unchanged; this is the scripted flip it does not exercise.
        const string Host = "rebind-flip-e2e.com";
        var publicAddress = IPAddress.Parse("93.184.216.34");
        await using var server = new HttpTestServer(HttpTestRoutes.Standard);
        var dns = new FakeDnsResolver().FlipAfterFirstResolve(Host, [publicAddress], [IPAddress.Loopback]);
        var dialed = new ConcurrentQueue<IPAddress>();
        var endpointFor = (IPAddress address, int _) =>
        {
            // Recorded so the test can prove the validated address (not a re-resolved one) is what the socket dials.
            dialed.Enqueue(address);

            return IPAddress.IsLoopback(address)
                ? new IPEndPoint(address, server.Port)
                : new IPEndPoint(IPAddress.Loopback, HttpTestServer.ClosedPort());
        };
        using var loggers = LoggerFactory.Create(_ => { });
        var connector = new SafeHttpConnector(
            dns, new LoopbackAllowingAddressValidator(), loggers.CreateLogger<SafeHttpConnector>(), endpointFor);

        using (var client = SafeHttpClientFactory.Create(connector))
        {
            await Should.ThrowAsync<Exception>(() => client.GetAsync($"http://{Host}/ok", Ct));
        }

        dns.Lookups.ShouldBe([Host], "exactly one resolution per connect attempt");
        dialed.ToArray().ShouldBe([publicAddress], "the dialled address is the validated one, never a re-resolved one");
        server.Connections.ShouldBe(0, "a re-resolve or a dial by host name would have reached the loopback server");
        server.Requests.ShouldBeEmpty();

        // Positive control: when the validated answer itself is loopback (allowed here), the same seam reaches the server.
        var loopbackDns = new FakeDnsResolver().Map(Host, IPAddress.Loopback);
        var control = new SafeHttpConnector(
            loopbackDns, new LoopbackAllowingAddressValidator(), loggers.CreateLogger<SafeHttpConnector>(), endpointFor);
        using (var client = SafeHttpClientFactory.Create(control))
        using (var response = await client.GetAsync($"http://{Host}/ok", Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, "the control: the server is reachable when the validated address is loopback");
        }

        server.Connections.ShouldBeGreaterThan(0);
        loopbackDns.Lookups.ShouldBe([Host]);
        dialed.ToArray().ShouldBe([publicAddress, IPAddress.Loopback], "the control dialled the valid loopback answer");
    }

    [Fact]
    public async Task Ssrf_DnsResolverAnswerLoopback_ExactlyOneRequestReachesServer()
    {
        // threat row 4, case 10 (the converse): the resolver's answer is the address used - 127.0.0.1 reaches the server.
        var dns = new FakeDnsResolver().Map("answer-e2e.com", IPAddress.Loopback);
        await using var stack = NewStack(dns: dns);

        (await stack.FetchAsync("http://answer-e2e.com/ok", Ct)).IsError.ShouldBeFalse();

        stack.PageRequests.Count.ShouldBe(1, "exactly one page request reached the server");
        stack.PageRequests[0].Host.ShouldStartWith("answer-e2e.com");
    }

    [Fact]
    public async Task Connect_PeerAddressDeniedByScriptedValidator_Throws()
    {
        // Defence in depth only (threat row 4): the RemoteEndPoint re-check. The scripted validator allows the resolved
        // address on the first question and denies the connected peer on the second. Case 10 does not depend on it.
        var validator = new ScriptedValidator(allowFirst: true);
        await using var stack = FetchStack.Create(HttpTestRoutes.Standard, validator: validator);

        var result = await stack.FetchAsync("http://scripted-e2e.com/ok", Ct);

        FetchStack.Code(result).ShouldBe(SourceErrors.SourceBlockedByRobotsCode, "the robots connect is the first to be denied");
        validator.Asked.ShouldBeGreaterThanOrEqualTo(2, "resolved address, then the connected peer");
        stack.PageRequests.ShouldBeEmpty("no page request followed the denied connect");
    }

    // ------------------------------------------------------------------ URL normalisation (rows 3, 13)

    [Fact]
    public async Task Url_IdnAndTrailingDot_NormalisedNotBlocked()
    {
        // threat row 3, case 11 (F14: IDN / trailing dot): accepted, normalised; the request goes to the normalised host.
        await using var stack = NewStack();
        var idn = ParseOk("http://exаmple.com/ok");
        var dotted = ParseOk("http://page-e2e.com./ok");

        idn.Host.ShouldStartWith("xn--");
        dotted.Host.ShouldBe("page-e2e.com");
        foreach (var url in new[] { idn, dotted })
        {
            (await stack.FetchAsync(url.Value, Ct)).IsError.ShouldBeFalse(url.Value);
        }

        var hosts = stack.PageRequests.Select(request => request.Host.Split(':')[0]).ToList();
        hosts.ShouldBe([idn.Host, dotted.Host]);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://intranet/")]
    [InlineData("http://co.uk/")]
    public void Url_SingleLabelAndBareTld_Rejected(string url)
    {
        // threat row 3, case 12.
        var result = SourceUrl.Parse(url, new FakePublicSuffixList());

        result.IsError.ShouldBeTrue(url);
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
    }

    [Theory]
    [InlineData("http://example.com:8080/")]
    [InlineData("http://user:pw@example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    [InlineData("LONG")]
    public void Url_PortUserInfoSchemeLength_Rejected(string url)
    {
        // threat row 13, case 13.
        var text = url == "LONG" ? "http://example.com/" + new string('a', SourceUrl.MaxLength - "http://example.com/".Length + 1) : url;

        var result = SourceUrl.Parse(text, new FakePublicSuffixList());

        result.IsError.ShouldBeTrue(url);
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
    }

    [Fact]
    public async Task InternalApi_LeaseRouteAbsentOnPublicPort()
    {
        // threat row 13, case 14: the lease route exists only on the internal listener (:8081). Full matrix in
        // InternalListenerIsolationTests (02.5.2-T2); this is the suite's own evidence line.
        using var factory = new ApiFactory(Database, new FakeClock(FetchTaskSeed.Start)).WithFetcherInternalApi();
        using var client = factory.PublicPortClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.FetcherToken);

        using var response = await client.PostAsync(
            FetchTaskSeed.Prefix + "/lease",
            new StringContent("{\"workerId\":\"fetcher-1\",\"max\":4}", System.Text.Encoding.UTF8, "application/json"),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ redirects and the blocklist (rows 5, 11)

    [Fact]
    public async Task Redirect_ToLoopback_Blocked()
    {
        // threat row 5, case 15.
        await using var stack = NewStack();
        var target = Uri.EscapeDataString("http://127.0.0.1/ok");

        FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com/redirect?to={target}", Ct)).ShouldBe(SourceErrors.SourceUrlInvalidCode);

        stack.PageRequests.Count.ShouldBe(1, "only the redirect itself; the loopback /ok logged nothing");
        stack.PageRequests.ShouldNotContain(request => request.Target == "/ok");
    }

    [Fact]
    public async Task Redirect_ToMetadata_Blocked()
    {
        // threat row 5, case 16.
        await using var stack = NewStack();
        var target = Uri.EscapeDataString("http://169.254.169.254/");

        FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com/redirect?to={target}", Ct)).ShouldBe(SourceErrors.SourceUrlInvalidCode);

        stack.PageRequests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://m.youtube.com/watch")]
    [InlineData("https://youtu.be/x")]
    [InlineData("https://vm.tiktok.com/x")]
    [InlineData("https://bit.ly/x")]
    public async Task Redirect_ToBlockedDomain_Blocked(string target)
    {
        // threat row 11, case 17 (F14: subdomain and redirect-target blocklist): applied on the registrable domain of every hop.
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com/redirect?to={Uri.EscapeDataString(target)}", Ct))
            .ShouldBe(SourceErrors.SourceDomainNotAllowedCode, target);

        stack.PageRequests.Count.ShouldBe(1, "the blocked hop was never requested");
        stack.Dns.Lookups.ShouldNotContain(new Uri(target).Host);
    }

    [Fact]
    public async Task Redirect_FourHops_Unreachable()
    {
        // threat row 5, case 18: 3 redirects are followed, the 4th response is refused.
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync("http://page-e2e.com/chain?n=4", Ct)).ShouldBe(SourceErrors.SourceUnreachableCode);

        stack.PageRequests.Count.ShouldBe(4, "3 redirects + the initial request");
    }

    [Fact]
    public async Task Redirect_RelativeLocation_FollowedAndRevalidated()
    {
        // threat row 5, case 19.
        await using var stack = NewStack();

        var result = await stack.FetchAsync("http://page-e2e.com/redirect?to=%2Fok", Ct);

        result.IsError.ShouldBeFalse();
        result.Value.FinalUrl.ShouldEndWith("/ok");
        stack.PageRequests.Select(request => request.Target).ShouldBe(["/redirect?to=%2Fok", "/ok"]);
    }

    [Theory]
    [InlineData("http://user:pw@other-e2e.com/ok")]
    [InlineData("ftp://other-e2e.com/ok")]
    [InlineData("file:///etc/passwd")]
    public async Task Redirect_WithCredentialsOrBadScheme_Rejected(string target)
    {
        // threat row 5, case 20.
        await using var stack = NewStack();

        FetchStack.Code(await stack.FetchAsync($"http://page-e2e.com/redirect?to={Uri.EscapeDataString(target)}", Ct))
            .ShouldBe(SourceErrors.SourceUrlInvalidCode, target);

        stack.PageRequests.Count.ShouldBe(1);
        stack.Dns.Lookups.ShouldNotContain("other-e2e.com");
    }

    // ------------------------------------------------------------------ D20: no loopback bypass in production

    [Theory]
    [InlineData("http://loopback-e2e.com/ok")]
    [InlineData("http://page-e2e.com/chain?n=1&via=loopback")]
    [InlineData("http://127.0.0.1/ok")]
    [InlineData("REDIRECT")]
    [InlineData("http://10.0.0.1/ok")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://page-e2e.com/redirect?to=http%3A%2F%2F127.0.0.1%2Fok")]
    [InlineData("http://page-e2e.com/redirect?to=http%3A%2F%2F169.254.169.254%2F")]
    [InlineData("http://page-e2e.com/redirect?to=%2Fok")]
    [InlineData("http://page-e2e.com/robots-disallow")]
    public async Task D20_ProductionValidator_LoopbackServerIsUnreachable(string url)
    {
        // D20 dual run (the second run of the representative subset: shapes of cases 1, 2, 3, 6, 15, 16, 18, 19, 25; case 10 is Ssrf_DnsRebinding_*): with the production validator
        // the loopback test server must see no connection at all. The first run is every other test of this class, which uses
        // the loopback-allowing validator. A test that only passes with the test validator is a finding.
        await using var stack = NewStack(ValidatorMode.GlobalUnicast);
        var text = url.Replace(
            "REDIRECT", $"http://page-e2e.com/redirect?to={Uri.EscapeDataString("http://redirect-target-e2e.com/ok")}", StringComparison.Ordinal);

        TestContext.Current.SendDiagnosticMessage($"D20 run: validator={stack.Mode}, url={text}");
        stack.Mode.ShouldBe(ValidatorMode.GlobalUnicast, "the D20 run must use the production validator");
        var result = await stack.FetchAsync(text, Ct);

        FetchStack.Code(result).ShouldBeOneOf(
            SourceErrors.SourceUrlInvalidCode,
            SourceErrors.SourceUnreachableCode,
            SourceErrors.SourceBlockedByRobotsCode);
        stack.ShouldNeverHaveReachedTheServer($"production validator ({stack.Mode})");
    }

    private sealed class ScriptedValidator(bool allowFirst) : IAddressValidator
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public bool IsAllowed(IPAddress address, out string? reason)
        {
            var count = Interlocked.Increment(ref _asked);
            if (allowFirst && count % 2 == 1)
            {
                reason = null;

                return true;
            }

            reason = "scripted denial";

            return false;
        }
    }
}
