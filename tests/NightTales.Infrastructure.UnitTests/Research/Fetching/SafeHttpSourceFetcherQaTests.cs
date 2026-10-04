using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research;
using NightTales.Infrastructure.Research.Extraction;
using NightTales.Infrastructure.Research.Http;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>
/// QA additions for 02.5.2-T1 (the T3 review matrix): the SSRF and parser cases the first suite did not
/// cover — non-HTTP redirect schemes, every IP-literal notation as a redirect target (including the last
/// hop of a chain), a robots.txt redirected to another host or an IP literal, a response whose real body
/// exceeds the byte cap without <c>Content-Length</c> (close and chunked), an encoding the fetcher does
/// not advertise (deflate), a dribbled (slowloris) body bounded by the shared total deadline, a declared
/// charset that is wrong for the bytes, and proof that <c>HTTP_PROXY</c> env vars cannot reroute the
/// connector. Loopback test servers only; nothing touches the real network.
/// </summary>
public sealed class SafeHttpSourceFetcherQaTests
{
    private const string Html = "text/html; charset=utf-8";

    private static string Code(ErrorOr<FetchedSource> result)
    {
        result.IsError.ShouldBeTrue("expected a fetch error");

        return result.FirstError.Code;
    }

    private static TestResponse Article(string title = "Mùa thu Hà Nội") =>
        TestResponse.Text(200, Html,
            "<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><title>" + title + "</title></head><body><article><h1>"
            + title + "</h1><p>"
            + string.Concat(Enumerable.Repeat("Hà Nội những ngày cuối thu, hoa sữa nở thơm ngát khắp các con phố nhỏ của thành phố. ", 8))
            + "</p></article></body></html>");

    // ------------------------------------------------------------------
    // Redirect targets: every notation and scheme must be rejected and
    // never dialled. With the loopback-allowing validator and the fake DNS
    // answering loopback for every name, a target that slips past the
    // checks would produce a second page request — so the assertion that
    // no second request happened IS the SSRF proof.
    // ------------------------------------------------------------------

    /// <summary>IP-literal redirect targets: every notation in the F14 list, as the first hop.</summary>
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f.0.0.1/")]
    public async Task Fetch_RedirectToIpLiteralTarget_IsRejected_NeverDialled(string location)
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect(location));

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue($"the redirect to {location} must not be followed");
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
        h.Server.Requests.ShouldAllBe(r => r.Host.StartsWith("www.example.com", StringComparison.Ordinal),
            "no request may leave for the redirect target");
        h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(1);
    }

    /// <summary>The IP-literal check must also fire at the END of a multi-hop chain, not only hop one.</summary>
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://2130706433/")]
    public async Task Fetch_RedirectChainEndingAtIpLiteral_IsRejected(string finalTarget)
    {
        await using var h = FetchHarness.Create(request => request.Target switch
        {
            "/a" => TestResponse.Redirect("http://www.example.com/b"),
            "/b" => TestResponse.Redirect("http://www.example.com/c"),
            "/c" => TestResponse.Redirect(finalTarget),
            _ => Article(),
        });

        var result = await h.FetchAsync("http://www.example.com/a", ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUrlInvalidCode);
        h.Server.Requests.ShouldAllBe(r => r.Host.StartsWith("www.example.com", StringComparison.Ordinal));
        h.Server.Requests.Count.ShouldBe(3 + 1, "three page hops plus one robots.txt; the last hop is never dialled");
    }

    /// <summary>Non-HTTP redirect schemes: the hop re-parse rejects them before any connection.</summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://internal.example.net/secret")]
    [InlineData("javascript:alert(1)")]
    public async Task Fetch_RedirectToNonHttpScheme_ReturnsSourceUrlInvalid(string location)
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect(location));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUrlInvalidCode);
        h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(1);
    }

    /// <summary>A data: URL in Location must not be followed either (rejected, whatever the exact code).</summary>
    [Fact]
    public async Task Fetch_RedirectToDataUrl_IsNotFollowed()
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect("data:text/html;base64,PGI+b2s8L2I+"));

        (await h.FetchAsync(ct: TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(1);
    }

    /// <summary>A redirect to a non-80/443 port is rejected by the hop re-parse.</summary>
    [Theory]
    [InlineData("http://www.example.com:8080/admin")]
    [InlineData("http://127.0.0.1:6379/")]
    public async Task Fetch_RedirectToNonStandardPort_ReturnsSourceUrlInvalid(string location)
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect(location));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUrlInvalidCode);
        h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(1);
    }

    // ------------------------------------------------------------------
    // DNS rebinding: the connect-time answer is re-validated at connect.
    // ------------------------------------------------------------------

    /// <summary>
    /// The name resolves PUBLIC the first time and PRIVATE at connect time: a
    /// <see cref="GlobalUnicastAddressValidator"/> (no loopback bypass) sees and denies the private
    /// answer, so nothing is dialled. Proves the check runs on the address used for the connection,
    /// not on an earlier (or cached) answer.
    /// </summary>
    [Fact]
    public async Task Connect_ResolverAnswersPublicThenPrivate_CheckRunsOnConnectTimeAddress()
    {
        var server = new HttpTestServer(_ => TestResponse.Text(200, "text/plain", "hello"));
        await using (server)
        {
            var dns = new SequencedDnsResolver(
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("10.0.0.7"));
            var validator = new RecordingValidator(new GlobalUnicastAddressValidator());
            using var http = SafeHttpClientFactory.Create(new SafeHttpConnector(
                dns,
                validator,
                NullLogger<SafeHttpConnector>.Instance,
                server.EndpointFor(HttpTestServer.ClosedPort())));
            var ct = TestContext.Current.CancellationToken;

            var first = await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync("http://rebind.example.com/", ct));
            SafeHttp.FindFetchException(first).ShouldNotBeNull().Error.Code.ShouldBe(SourceErrors.SourceUnreachableCode,
                "the public answer is validated but the dial is refused at the test closed port");

            var second = await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync("http://rebind.example.com/", ct));
            SafeHttp.FindFetchException(second).ShouldNotBeNull().Error.Code.ShouldBe(SourceErrors.SourceUnreachableCode,
                "the private connect-time answer is denied");

            validator.Seen.ShouldContain(ip => ip.ToString() == "10.0.0.7",
                "the private connect-time address was actually checked");
            server.Connections.ShouldBe(0, "the private address is never dialled");
            dns.Lookups.ShouldBe(2, "the name is resolved per connection, not cached from hop one");
        }
    }

    /// <summary>
    /// Fetcher level: a redirect target whose DNS answer is private is denied at connect time, end to end
    /// through the real pipeline (robots + rate limiter faked, connect and validation real).
    /// </summary>
    [Fact]
    public async Task Fetch_RedirectHopResolvesPrivate_DeniedAtConnectTime()
    {
        var server = new HttpTestServer(request => request.Target switch
        {
            "/robots.txt" => new TestResponse(404),
            "/a" => TestResponse.Redirect("http://b.example.com/x"),
            _ => Article(),
        });
        var (stack, dispose) = await CreateStackAsync(server, validator: null);
        await using var stackScope = dispose;
        stack.Dns.Map("b.example.com", IPAddress.Parse("192.168.1.1"));
        var result = await stack.Fetcher.FetchAsync(
            new SourceFetchRequest(new Uri("http://www.example.com/a"), "example.com", 1),
            TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBeOneOf(SourceErrors.SourceBlockedByRobotsCode, SourceErrors.SourceUnreachableCode);
        // D11: the hop's robots.txt fetch hits the connect-time denial for 192.168.1.1, which is a robots
        // fetch failure (disallow all); either way 192.168.1.1 is never dialled and b.example.com never served.
        server.Requests.ShouldAllBe(r => r.Host.StartsWith("www.example.com", StringComparison.Ordinal),
            "b.example.com was never reached");
    }

    // ------------------------------------------------------------------
    // robots.txt edge cases.
    // ------------------------------------------------------------------

    /// <summary>RFC 9309: a robots.txt redirected to ANOTHER host applies that host's rules to the page origin.</summary>
    [Fact]
    public async Task Fetch_RobotsRedirectToOtherHost_AppliesFinalRules_ToPageOrigin()
    {
        await using var h = FetchHarness.Create(_ => Article());
        h.Robots = request => request.Host switch
        {
            var host when host.StartsWith("www.example.com", StringComparison.Ordinal) =>
                TestResponse.Redirect("http://cdn.example.net/robots.txt"),
            _ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /\n"),
        };

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);

        var robotsRequests = h.Server.Requests.Where(r => r.Target == "/robots.txt").ToList();
        robotsRequests.Count.ShouldBe(2);
        robotsRequests[0].Host.ShouldStartWith("www.example.com");
        robotsRequests[1].Host.ShouldStartWith("cdn.example.net");
        h.Server.Requests.ShouldAllBe(r => r.Target == "/robots.txt", "the page itself is never fetched");
    }

    /// <summary>A robots.txt redirected to an IP literal is refused at the hop re-check and denies the page.</summary>
    [Fact]
    public async Task Fetch_RobotsRedirectToIpLiteral_Disallows()
    {
        await using var h = FetchHarness.Create(_ => Article());
        h.Robots = request => request.Target switch
        {
            "/robots.txt" => TestResponse.Redirect("http://169.254.169.254/robots.txt"),
            _ => Article(),
        };

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        h.Server.Requests.ShouldAllBe(r => r.Host.StartsWith("www.example.com", StringComparison.Ordinal));
        h.Server.Requests.Count.ShouldBe(1, "the IP-literal hop is never dialled");
    }

    /// <summary>A robots.txt redirected to a non-80/443 port is refused at the hop re-check.</summary>
    [Fact]
    public async Task Fetch_RobotsRedirectToNonStandardPort_Disallows()
    {
        await using var h = FetchHarness.Create(_ => Article());
        h.Robots = _ => TestResponse.Redirect("http://www.example.com:8443/robots.txt");

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        h.Server.Requests.ShouldAllBe(r => r.Target == "/robots.txt");
    }

    // ------------------------------------------------------------------
    // Cross-host redirect: no cookies, no auth, no referer leaves for the
    // next host.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fetch_CrossHostRedirect_SendsNoCookieAuthOrReferer()
    {
        await using var h = FetchHarness.Create(request => request.Target switch
        {
            "/start" => TestResponse.Redirect("http://cdn.example.net/final") with
            {
                Headers = new Dictionary<string, string>
                {
                    ["Location"] = "http://cdn.example.net/final",
                    ["Set-Cookie"] = "sid=xyz; Path=/",
                },
            },
            _ => Article(),
        });

        (await h.FetchAsync("http://www.example.com/start", ct: TestContext.Current.CancellationToken))
            .IsError.ShouldBeFalse();

        var secondHost = h.Server.Requests.Where(r => r.Host.StartsWith("cdn.example.net", StringComparison.Ordinal)).ToList();
        secondHost.Count.ShouldBe(2, "the redirected robots.txt and the page");
        foreach (var request in secondHost)
        {
            request.Headers.ContainsKey("Cookie").ShouldBeFalse();
            request.Headers.ContainsKey("Authorization").ShouldBeFalse();
            request.Headers.ContainsKey("Referer").ShouldBeFalse();
        }
    }

    // ------------------------------------------------------------------
    // Proxy env vars must not bypass the connector.
    // ------------------------------------------------------------------

    /// <summary>
    /// With <c>HTTP_PROXY</c>/<c>http_proxy</c> pointing at a dead proxy, a fetch still completes through
    /// the connect callback — proving <c>UseProxy = false</c> really isolates the client from the
    /// environment. Mutates process env; restores it in <see langword="finally"/>.
    /// </summary>
    [Fact]
    public async Task Fetch_ProxyEnvVarsSet_DoesNotRerouteThroughProxy()
    {
        var httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var httpProxyLower = Environment.GetEnvironmentVariable("http_proxy");
        var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var httpsProxyLower = Environment.GetEnvironmentVariable("https_proxy");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:9");
            Environment.SetEnvironmentVariable("http_proxy", "http://127.0.0.1:9");
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:9");
            Environment.SetEnvironmentVariable("https_proxy", "http://127.0.0.1:9");

            await using var h = FetchHarness.Create(_ => Article());
            var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

            result.IsError.ShouldBeFalse("UseProxy=false: the env proxy at the dead port 9 is ignored");
            h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", httpProxy);
            Environment.SetEnvironmentVariable("http_proxy", httpProxyLower);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", httpsProxy);
            Environment.SetEnvironmentVariable("https_proxy", httpsProxyLower);
        }
    }

    // ------------------------------------------------------------------
    // Body size and encoding lies — need raw control of the wire, so a
    // small raw HTTP/1.1 responder replaces HttpTestServer here.
    // ------------------------------------------------------------------

    /// <summary>No Content-Length, body closed by the connection: the decompressed byte cap still stops it.</summary>
    [Fact]
    public async Task Fetch_BodyExceedsCap_WithoutContentLength_ReturnsSourceTooLarge()
    {
        var payload = new byte[128 * 1024];
        Array.Fill(payload, (byte)'a');
        var server = new RawHttpServer(async (stream, stop) =>
        {
            await WriteAsciiAsync(stream,
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n", stop);
            for (var i = 0; i < 4; i++)
            {
                await stream.WriteAsync(payload, stop);
            }
        });
        await using (server)
        {
            var (stack, dispose) = await CreateStackAsync(server, configure: o => o.MaxResponseBytes = 64 * 1024);
            await using var stackScope = dispose;

            Code(await FetchRawAsync(stack, TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
        }
    }

    /// <summary>Chunked encoding (no Content-Length) over the cap: counted and refused the same way.</summary>
    [Fact]
    public async Task Fetch_BodyExceedsCap_Chunked_ReturnsSourceTooLarge()
    {
        var chunk = new byte[32 * 1024];
        Array.Fill(chunk, (byte)'a');
        var server = new RawHttpServer(async (stream, stop) =>
        {
            await WriteAsciiAsync(stream,
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n", stop);
            var head = Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n");
            for (var i = 0; i < 8; i++)
            {
                await stream.WriteAsync(head, stop);
                await stream.WriteAsync(chunk, stop);
                await WriteAsciiAsync(stream, "\r\n", stop);
            }

            await WriteAsciiAsync(stream, "0\r\n\r\n", stop);
        });
        await using (server)
        {
            var (stack, dispose) = await CreateStackAsync(server, configure: o => o.MaxResponseBytes = 64 * 1024);
            await using var stackScope = dispose;

            Code(await FetchRawAsync(stack, TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
        }
    }

    /// <summary>
    /// A deflate body is read as-is: the fetcher advertises only <c>gzip, br</c> in
    /// <c>Accept-Encoding</c>, so a server answering deflate anyway produces bytes that are not text —
    /// the sniff fails rather than the bomb being expanded or the page accepted.
    /// </summary>
    [Fact]
    public async Task Fetch_DeflateEncoded_NotAdvertised_FailsTheSniff()
    {
        byte[] deflated;
        await using (var buffer = new MemoryStream())
        {
            await using (var zip = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("plain article text ", 500)));
                zip.Write(text);
            }

            deflated = buffer.ToArray();
        }

        await using var h = FetchHarness.Create(_ => new TestResponse(200, "text/html", deflated)
        {
            Headers = new Dictionary<string, string> { ["Content-Encoding"] = "deflate" },
        });

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SourceErrors.SourceUnsupportedTypeCode);
    }

    // ------------------------------------------------------------------
    // Slowloris: the shared total deadline, not any per-read timeout,
    // bounds a response that arrives one dribble at a time.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fetch_SlowDribbleBody_TotalDeadlineBoundsTheFetch()
    {
        var server = new RawHttpServer(async (stream, stop) =>
        {
            await WriteAsciiAsync(stream,
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: 64\r\nConnection: close\r\n\r\n", stop);
            var drip = Encoding.ASCII.GetBytes("01234567");
            for (var i = 0; i < 8 && !stop.IsCancellationRequested; i++)
            {
                await stream.WriteAsync(drip, stop);
                await stream.FlushAsync(stop);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
        await using (server)
        {
            var (stack, dispose) = await CreateStackAsync(server, configure: o => o.TotalTimeoutSeconds = 1);
            await using var stackScope = dispose;
            var clock = Stopwatch.StartNew();

            Code(await FetchRawAsync(stack, TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
                "the shared total budget (~1 s), not the ~4 s dribble, bounds the body read");
        }
    }

    // ------------------------------------------------------------------
    // Charset edge cases.
    // ------------------------------------------------------------------

    /// <summary>No charset in the header and no meta tag: the UTF-8 fallback decodes the page.</summary>
    [Fact]
    public async Task Fetch_NoCharsetAnywhere_Utf8Fallback_Decodes()
    {
        var captured = string.Empty;
        var extractor = new StubExtractor(html =>
        {
            captured = html;
            return new ArticleExtraction("t", html[..Math.Min(html.Length, 100)], 100, IsMainTextFound: true);
        });
        var body = "<!doctype html><html><body><p>Nguyễn hoa sữa mùa thu</p></body></html>";
        await using var h = FetchHarness.Create(_ => TestResponse.Text(200, "text/html", body), extractor: extractor);

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        captured.Contains("Nguyễn", StringComparison.Ordinal).ShouldBeTrue("no header charset and no meta → UTF-8 with replacement");
    }

    /// <summary>
    /// A declared charset that is wrong for the bytes (us-ascii vs UTF-8) is honoured for decoding —
    /// and the strict sniff of the first 512 bytes fails the body as not text before extraction.
    /// </summary>
    [Fact]
    public async Task Fetch_WrongDeclaredCharset_FailsTheSniff()
    {
        var extractor = new StubExtractor(_ => new ArticleExtraction("t", "ok", 100, IsMainTextFound: true));
        var body = "<!doctype html><html><body><p>Nguyễn hoa sữa mùa thu</p></body></html>";
        await using var h = FetchHarness.Create(_ => TestResponse.Text(200, "text/html; charset=us-ascii", body), extractor: extractor);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnsupportedTypeCode);
        extractor.Calls.ShouldBe(0, "the byte sniff runs before extraction");
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static async Task WriteAsciiAsync(NetworkStream stream, string text, CancellationToken ct) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct);

    /// <summary>A raw responder over <paramref name="server"/> + the whole fetch stack (real robots).</summary>
    private static async Task<(Stack Stack, IAsyncDisposable Dispose)> CreateStackAsync(
        RawHttpServer server,
        Action<FetcherOptions>? configure = null,
        IAddressValidator? validator = null)
    {
        var options = FetchHarness.DefaultOptions();
        configure?.Invoke(options);
        var dns = new FakeDnsResolver { DefaultAnswer = [IPAddress.Loopback] };
        var logs = new ListLoggerProvider();
        var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var connector = new SafeHttpConnector(
            dns,
            validator ?? new LoopbackAllowingAddressValidator(),
            factory.CreateLogger<SafeHttpConnector>(),
            (address, _) => IPAddress.IsLoopback(address)
                ? new IPEndPoint(address, server.Port)
                : new IPEndPoint(IPAddress.Loopback, HttpTestServer.ClosedPort()));
        var http = SafeHttpClientFactory.Create(connector);
        var psl = new FakePublicSuffixList();
        var blocklist = new DomainBlocklist(options.BlockedDomains.ToHashSet(StringComparer.Ordinal));
        var robots = new RobotsTxtPolicy(http, psl, new FakeHostRateLimiter(), blocklist, FetchHarness.Options2(options), TimeProvider.System,
            factory.CreateLogger<RobotsTxtPolicy>());
        var stack = new Stack(
            new SafeHttpSourceFetcher(
                http,
                new SmartReaderArticleExtractor(FetchHarness.Options2(options)),
                robots,
                new FakeHostRateLimiter(),
                blocklist,
                psl,
                FetchHarness.Options2(options),
                factory.CreateLogger<SafeHttpSourceFetcher>()),
            http,
            dns);
        await Task.CompletedTask;

        return (stack, new DisposeAll(http));
    }

    private static async Task<(Stack Stack, IAsyncDisposable Dispose)> CreateStackAsync(
        HttpTestServer server,
        IAddressValidator? validator)
    {
        var options = FetchHarness.DefaultOptions();
        var dns = new FakeDnsResolver { DefaultAnswer = [IPAddress.Loopback] };
        var logs = new ListLoggerProvider();
        var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var connector = new SafeHttpConnector(
            dns,
            validator ?? new LoopbackAllowingAddressValidator(),
            factory.CreateLogger<SafeHttpConnector>(),
            server.EndpointFor(HttpTestServer.ClosedPort()));
        var http = SafeHttpClientFactory.Create(connector);
        var psl = new FakePublicSuffixList();
        var blocklist = new DomainBlocklist(options.BlockedDomains.ToHashSet(StringComparer.Ordinal));
        var robots = new RobotsTxtPolicy(http, psl, new FakeHostRateLimiter(), blocklist, FetchHarness.Options2(options), TimeProvider.System,
            factory.CreateLogger<RobotsTxtPolicy>());
        var stack = new Stack(
            new SafeHttpSourceFetcher(
                http,
                new SmartReaderArticleExtractor(FetchHarness.Options2(options)),
                robots,
                new FakeHostRateLimiter(),
                blocklist,
                psl,
                FetchHarness.Options2(options),
                factory.CreateLogger<SafeHttpSourceFetcher>()),
            http,
            dns);
        await Task.CompletedTask;

        return (stack, new DisposeAll(http));
    }

    private static ValueTask<ErrorOr<FetchedSource>> FetchRawAsync(Stack stack, CancellationToken ct) =>
        stack.Fetcher.FetchAsync(
            new SourceFetchRequest(new Uri("http://www.example.com/a"), "example.com", 1), ct);

    private sealed record Stack(SafeHttpSourceFetcher Fetcher, HttpClient Http, FakeDnsResolver Dns);

    private sealed class DisposeAll(HttpClient http) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            http.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An <see cref="IDnsResolver"/> that plays a fixed answer sequence for every host.</summary>
    private sealed class SequencedDnsResolver(params IPAddress[] answers) : IDnsResolver
    {
        private int _call;

        public int Lookups => _call;

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _call) - 1;

            return ValueTask.FromResult<IReadOnlyList<IPAddress>>([answers[Math.Min(index, answers.Length - 1)]]);
        }
    }

    /// <summary>An <see cref="IAddressValidator"/> decorator that records every address checked and the verdict.</summary>
    private sealed class RecordingValidator(IAddressValidator inner) : IAddressValidator
    {
        private readonly List<(IPAddress Address, bool Allowed)> _seen = [];

        public IReadOnlyList<IPAddress> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen.Select(entry => entry.Address)];
                }
            }
        }

        public bool IsAllowed(IPAddress address, out string? reason)
        {
            var allowed = inner.IsAllowed(address, out reason);
            lock (_seen)
            {
                _seen.Add((address, allowed));
            }

            return allowed;
        }
    }

    /// <summary>
    /// A minimal raw HTTP/1.1 loopback responder for the wire shapes <see cref="HttpTestServer"/> cannot
    /// produce (no Content-Length, chunked, dribbled writes). One scripted responder serves every accepted
    /// connection; the first (and here only) request line is recorded.
    /// </summary>
    private sealed class RawHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<string> _requestLines = [];
        private readonly List<Task> _clients = [];
        private readonly Task _accepting;

        /// <summary>
        /// Starts the server; <paramref name="respond"/> gets the client stream and must write the full
        /// response. Client-gone and shutdown errors inside the responder are swallowed.
        /// </summary>
        public RawHttpServer(Func<NetworkStream, CancellationToken, Task> respond)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        break;
                    }

                    lock (_clients)
                    {
                        _clients.Add(Task.Run(() => ServeAsync(client, respond)));
                    }
                }
            });
        }

        public int Port { get; }

        public IReadOnlyList<string> RequestLines => [.. _requestLines];

        private async Task ServeAsync(TcpClient client, Func<NetworkStream, CancellationToken, Task> respond)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = new List<byte>();
                    var one = new byte[1];
                    while (head.Count < 16 * 1024)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0)
                        {
                            return;
                        }

                        head.Add(one[0]);
                        var n = head.Count;
                        if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                        {
                            break;
                        }
                    }

                    _requestLines.Add(Encoding.ASCII.GetString([.. head]).Split("\r\n")[0]);
                    await respond(stream, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // The client hung up after the byte cap or the deadline — expected.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _accepting;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            Task[] clients;
            lock (_clients)
            {
                clients = [.. _clients];
            }

            await Task.WhenAll(clients);
            _stop.Dispose();
        }
    }
}
