using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.Core.Research;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.TestKit.Fetching;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>The robots.txt policy over a real loopback server (02 D11 failure semantics, 24 h per-origin cache).</summary>
public sealed class RobotsTxtPolicyTests : IAsyncDisposable
{
    private static readonly Uri Page = new("http://www.example.com/a/page");

    private readonly ManualTimeProvider _time = new();
    private readonly FakeHostRateLimiter _limiter = new();
    private readonly List<HttpTestServer> _servers = [];
    private readonly List<HttpClient> _clients = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task MissingRobots_AllowsAll()
    {
        var (policy, _) = Create(_ => new TestResponse(404));

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeTrue();
        decision.MinInterval.ShouldBe(RobotsTxtPolicy.MinIntervalFloor);
    }

    [Fact]
    public async Task ServerError_DisallowsAll()
    {
        var (policy, _) = Create(_ => new TestResponse(500));

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task DisallowRoot_Disallows_AndAllowsOtherPathWhenOnlyPartlyBlocked()
    {
        var (policy, _) = Create(_ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /a/\n"));
        var ct = TestContext.Current.CancellationToken;

        (await policy.IsAllowedAsync(Page, ct)).Allowed.ShouldBeFalse();
        (await policy.IsAllowedAsync(new Uri("http://www.example.com/b/page"), ct)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task RedirectChainRevalidated()
    {
        var (policy, server) = Create(request => request.Target switch
        {
            "/robots.txt" => TestResponse.Redirect("http://www.youtube.com/robots.txt"),
            _ => new TestResponse(404),
        });

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse("a redirect to a blocked domain is re-checked and refused");
        server.Requests.Count.ShouldBe(1, "the blocked hop is never requested");
    }

    [Fact]
    public async Task RedirectChain_FollowedWithinThreeHops()
    {
        var (policy, server) = Create(request => request.Target switch
        {
            "/robots.txt" => TestResponse.Redirect("http://www.example.com/r1"),
            "/r1" => TestResponse.Redirect("/r2"),
            "/r2" => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /\n"),
            _ => new TestResponse(404),
        });

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
        server.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RedirectLoop_ExceedsHops_Disallows()
    {
        var (policy, _) = Create(_ => TestResponse.Redirect("http://www.example.com/robots.txt"));

        (await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Cache_SecondCallSameOrigin_NoSecondFetch()
    {
        var (policy, server) = Create(_ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow:\n"));
        var ct = TestContext.Current.CancellationToken;

        await policy.IsAllowedAsync(Page, ct);
        await policy.IsAllowedAsync(new Uri("http://www.example.com/other"), ct);

        server.Requests.Count.ShouldBe(1);
        policy.CachedOrigins.ShouldBe(1);
    }

    [Fact]
    public async Task RobotsRequest_WaitsOnTheSharedLimiter_ForTheRegistrableDomain_AtTheFloor()
    {
        // 02.5.2-T1 review CR-02.
        var (policy, _) = Create(_ => new TestResponse(404));

        await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        _limiter.Calls.ShouldHaveSingleItem().ShouldBe(("example.com", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RobotsRedirectHops_EachWaitOnTheLimiter_ForTheirOwnRegistrableDomain()
    {
        var (policy, server) = Create(request => request.Target switch
        {
            "/robots.txt" when request.Host.StartsWith("www.example.com", StringComparison.Ordinal) => TestResponse.Redirect("http://news.example.org/r1"),
            "/r1" => TestResponse.Redirect("/robots2.txt"),
            _ => new TestResponse(404),
        });

        await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        server.Requests.Count.ShouldBe(3);
        _limiter.Calls.Select(call => call.Domain).ShouldBe(["example.com", "example.org", "example.org"]);
    }

    [Fact]
    public async Task CachedVerdict_DoesNotTouchTheLimiterAgain()
    {
        var (policy, _) = Create(_ => new TestResponse(404));
        var ct = TestContext.Current.CancellationToken;

        await policy.IsAllowedAsync(Page, ct);
        await policy.IsAllowedAsync(Page, ct);

        _limiter.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PageRequestAfterTheRobotsFetch_WaitsOutTheInterval_OnTheSameLimiter()
    {
        // The real limiter shared by the robots fetch and the page: the first page request after a cold robots fetch must wait.
        var clock = new ManualTimeProvider();
        var limiter = new HostRateLimiter(clock);
        var (policy, _) = Create(_ => new TestResponse(404), limiter);
        var ct = TestContext.Current.CancellationToken;

        var decision = await policy.IsAllowedAsync(Page, ct);
        var page = limiter.WaitAsync("example.com", decision.MinInterval, ct).AsTask();

        page.IsCompleted.ShouldBeFalse("the robots request was recorded, so the page request waits");
        clock.Advance(decision.MinInterval);
        await page.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task Cache_DisallowVerdictIsCachedToo()
    {
        var (policy, server) = Create(_ => new TestResponse(503));
        var ct = TestContext.Current.CancellationToken;

        await policy.IsAllowedAsync(Page, ct);
        await policy.IsAllowedAsync(Page, ct);

        server.Requests.Count.ShouldBe(1, "a 5xx host must not be retried on every page");
    }

    [Fact]
    public async Task Cache_ExpiredAfter24h_Refetches()
    {
        var (policy, server) = Create(_ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow:\n"));
        var ct = TestContext.Current.CancellationToken;

        await policy.IsAllowedAsync(Page, ct);
        _time.Advance(TimeSpan.FromHours(23));
        await policy.IsAllowedAsync(Page, ct);
        server.Requests.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await policy.IsAllowedAsync(Page, ct);

        server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CrawlDelay_AboveFloor_IsReturnedAsMinInterval()
    {
        var (policy, _) = Create(_ => TestResponse.Text(200, "text/plain", "User-agent: *\nCrawl-delay: 3\n"));

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeTrue();
        decision.MinInterval.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CrawlDelay_BelowFloor_UsesOneSecond()
    {
        var (policy, _) = Create(_ => TestResponse.Text(200, "text/plain", "User-agent: *\nCrawl-delay: 0.5\n"));

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeTrue();
        decision.MinInterval.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CrawlDelay_AboveCap_Disallows()
    {
        var (policy, _) = Create(_ => TestResponse.Text(200, "text/plain", "User-agent: *\nCrawl-delay: 30\n"));

        var decision = await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task OversizedRobots_DisallowsAll()
    {
        var body = new byte[RobotsTxtPolicyOptions().MaxRobotsBytes + 1];
        Array.Fill(body, (byte)'#');
        var (policy, _) = Create(_ => new TestResponse(200, "text/plain", body));

        (await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task NonTextContentType_Disallows()
    {
        var (policy, _) = Create(_ => TestResponse.Text(200, "application/octet-stream", "User-agent: *\nDisallow:\n"));

        (await policy.IsAllowedAsync(Page, TestContext.Current.CancellationToken)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task OriginsAreCachedSeparately_ByHostAndScheme()
    {
        var (policy, server) = Create(_ => new TestResponse(404));
        var ct = TestContext.Current.CancellationToken;

        await policy.IsAllowedAsync(new Uri("http://www.example.com/a"), ct);
        await policy.IsAllowedAsync(new Uri("http://news.example.com/a"), ct);

        server.Requests.Count.ShouldBe(2);
        policy.CachedOrigins.ShouldBe(2);
    }

    private static FetcherOptions RobotsTxtPolicyOptions() => FetchHarness.DefaultOptions();

    private (RobotsTxtPolicy Policy, HttpTestServer Server) Create(Func<TestRequest, TestResponse> script, IHostRateLimiter? limiter = null)
    {
        var server = new HttpTestServer(script);
        _servers.Add(server);
        var dns = new FakeDnsResolver { DefaultAnswer = [IPAddress.Loopback] };
        var connector = new SafeHttpConnector(
            dns,
            new LoopbackAllowingAddressValidator(),
            NullLogger<SafeHttpConnector>.Instance,
            server.EndpointFor(HttpTestServer.ClosedPort()));
        var http = SafeHttpClientFactory.Create(connector);
        _clients.Add(http);
        var options = RobotsTxtPolicyOptions();
        var policy = new RobotsTxtPolicy(
            http,
            new FakePublicSuffixList(),
            limiter ?? _limiter,
            new DomainBlocklist(options.BlockedDomains.ToHashSet(StringComparer.Ordinal)),
            Options.Create(options),
            _time,
            NullLogger<RobotsTxtPolicy>.Instance);

        return (policy, server);
    }
}
