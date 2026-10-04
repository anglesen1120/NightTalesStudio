using NightTales.Infrastructure.Research.Http;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

public sealed class HostRateLimiterTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task SameDomain_TwoRequests_AtLeastOneSecondApart()
    {
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        var ct = TestContext.Current.CancellationToken;

        await limiter.WaitAsync("example.com", Floor, ct);
        var second = limiter.WaitAsync("example.com", Floor, ct).AsTask();

        await Task.Delay(50, ct);
        second.IsCompleted.ShouldBeFalse("the second request must wait for the interval");

        time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50, ct);
        second.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromMilliseconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task SameDomain_RealClock_TwoRequestsAreAtLeastOneSecondApart()
    {
        var limiter = new HostRateLimiter(TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        await limiter.WaitAsync("example.com", Floor, ct);
        var started = TimeProvider.System.GetTimestamp();
        await limiter.WaitAsync("example.com", Floor, ct);

        TimeProvider.System.GetElapsedTime(started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(950));
    }

    [Fact]
    public async Task DifferentDomains_DoNotBlock()
    {
        var limiter = new HostRateLimiter(new ManualTimeProvider());
        var ct = TestContext.Current.CancellationToken;

        await limiter.WaitAsync("example.com", Floor, ct);
        await limiter.WaitAsync("example.org", Floor, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    [Fact]
    public async Task IdleKey_Swept()
    {
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        await limiter.WaitAsync("example.com", Floor, TestContext.Current.CancellationToken);
        limiter.TrackedHosts.ShouldBe(1);

        time.Advance(HostRateLimiter.IdleTtl + TimeSpan.FromSeconds(1));
        limiter.Sweep();

        limiter.TrackedHosts.ShouldBe(0);
    }

    [Fact]
    public async Task CrawlDelayLargerThanFloor_UsesCrawlDelay()
    {
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        var ct = TestContext.Current.CancellationToken;
        var delay = TimeSpan.FromSeconds(3);

        await limiter.WaitAsync("example.com", delay, ct);
        var second = limiter.WaitAsync("example.com", delay, ct).AsTask();

        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50, ct);
        second.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task CrawlDelayBelowFloor_UsesOneSecond()
    {
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        var ct = TestContext.Current.CancellationToken;

        // The robots policy never returns less than the 1 s floor (RobotsTxtPolicyTests), so a 0.5 s delay arrives here as 1 s.
        await limiter.WaitAsync("example.com", Floor, ct);
        var second = limiter.WaitAsync("example.com", Floor, ct).AsTask();

        time.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(50, ct);
        second.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromMilliseconds(500));
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
