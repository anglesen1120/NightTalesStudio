using NightTales.Fetcher.Core.Research.Http;
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
    public async Task CancelledBeforeTheFirstAcquire_LeavesNoEntry()
    {
        var limiter = new HostRateLimiter(new ManualTimeProvider());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        for (var index = 0; index < 50; index++)
        {
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await limiter.WaitAsync($"host{index}.example.com", Floor, cancelled.Token));
        }

        limiter.TrackedHosts.ShouldBe(0, "a request cancelled before its first acquire must not leave an entry behind");
    }

    [Fact]
    public async Task CancelledWaits_LeaveNoEntryAfterTheIdleSweep()
    {
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        var ct = TestContext.Current.CancellationToken;

        // Cancel while the request may already hold an entry (the token fires at an arbitrary point of the call).
        for (var index = 0; index < 200; index++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wait = limiter.WaitAsync($"host{index}.example.com", Floor, cts.Token).AsTask();
            await cts.CancelAsync();
            try
            {
                await wait;
            }
            catch (OperationCanceledException)
            {
            }
        }

        time.Advance(HostRateLimiter.IdleTtl + TimeSpan.FromSeconds(1));
        limiter.Sweep();

        limiter.TrackedHosts.ShouldBe(0, "every entry, used or not, is swept once nobody holds it");
    }

    [Fact]
    public async Task SweepRunningWhileARequestAcquires_NeverLetsTheNextRequestSkipTheInterval()
    {
        // 02.5.2-T1 review CR-03: a sweep that checked "idle", then lost the race to an acquire, used to remove the
        // entry the request was using, so the next request got a fresh entry and ran with no wait.
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        var ct = TestContext.Current.CancellationToken;
        using var stop = new CancellationTokenSource();
        var sweeper = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    limiter.Sweep();
                }
            },
            ct);

        try
        {
            for (var iteration = 0; iteration < 1_000; iteration++)
            {
                // The entry (if the sweeper kept it) is now idle for longer than the TTL, so the sweeper may legitimately retire it.
                time.Advance(HostRateLimiter.IdleTtl + TimeSpan.FromSeconds(1));

                await limiter.WaitAsync("example.com", Floor, ct);
                var second = limiter.WaitAsync("example.com", Floor, ct).AsTask();

                second.IsCompleted.ShouldBeFalse($"iteration {iteration}: the second request must wait the interval, not run on a fresh entry");

                time.Advance(Floor);
                await second.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await sweeper;
        }
    }

    [Fact]
    public async Task AtCapacity_ANewDomainEvictsTheLeastRecentlyUsedIdleOne()
    {
        var time = new ManualTimeProvider();
        var limiter = new HostRateLimiter(time);
        var ct = TestContext.Current.CancellationToken;

        for (var index = 0; index < HostRateLimiter.MaxKeys; index++)
        {
            await limiter.WaitAsync($"host{index}.example.com", Floor, ct);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        limiter.TrackedHosts.ShouldBe(HostRateLimiter.MaxKeys);

        await limiter.WaitAsync("new.example.com", Floor, ct);

        limiter.TrackedHosts.ShouldBeLessThanOrEqualTo(HostRateLimiter.MaxKeys);

        // host0 was the least recently used, so it was evicted: a new request for it runs at once although no time passed.
        var first = limiter.WaitAsync("host0.example.com", Floor, ct).AsTask();
        first.IsCompleted.ShouldBeTrue("the evicted domain starts from a fresh entry");
        await first;
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
