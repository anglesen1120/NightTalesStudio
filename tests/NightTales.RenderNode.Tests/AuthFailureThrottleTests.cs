using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The auth-failure throttle of 05 D15 S2 (§10 T68): the tracked set is a real bound (PERF-01), the threshold is
/// crossed exactly once under concurrency, and a throttled attempt never extends its own window
/// (SEC-11.3.5-T1-02).
/// </summary>
public sealed class AuthFailureThrottleTests
{
    private const int MaxTrackedSources = 4096;
    private const int FailuresPerMinute = 10;
    private const string ApiKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void TrackedSources_NeverExceedTheCap()
    {
        var throttle = new AuthFailureThrottle(FailuresPerMinute, new TestTimeProvider());

        for (var index = 0; index < MaxTrackedSources + 700; index++)
        {
            throttle.RecordFailedAttempt(NextAddress(index)).ShouldBeFalse();
        }

        throttle.TrackedSourceCount.ShouldBeLessThanOrEqualTo(MaxTrackedSources);
    }

    [Fact]
    public void ExpiredSources_AreEvictedToMakeRoom()
    {
        var clock = new TestTimeProvider();
        var throttle = new AuthFailureThrottle(FailuresPerMinute, clock);
        for (var index = 0; index < MaxTrackedSources; index++)
        {
            throttle.RecordFailedAttempt(NextAddress(index));
        }

        clock.Advance(TimeSpan.FromSeconds(61));

        throttle.RecordFailedAttempt(NextAddress(MaxTrackedSources));

        throttle.TrackedSourceCount.ShouldBeLessThanOrEqualTo(MaxTrackedSources);
    }

    [Fact]
    public void ThrottledAttempt_DoesNotExtendTheWindow()
    {
        var clock = new TestTimeProvider();
        var throttle = new AuthFailureThrottle(FailuresPerMinute, clock);
        var source = IPAddress.Loopback;

        for (var attempt = 0; attempt < FailuresPerMinute; attempt++)
        {
            throttle.RecordFailedAttempt(source).ShouldBeFalse();
        }

        throttle.RecordFailedAttempt(source).ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));

        // The throttled attempt was not recorded, so the window is empty and the source is a plain 401 again.
        throttle.RecordFailedAttempt(source).ShouldBeFalse();
    }

    [Fact]
    public void ConcurrentFailures_CrossTheThresholdExactlyOnce()
    {
        for (var round = 0; round < 25; round++)
        {
            var throttle = new AuthFailureThrottle(FailuresPerMinute, new TestTimeProvider());
            var middleware = new ApiKeyMiddleware(
                _ => Task.CompletedTask,
                Options.Create(new RenderNodeServerOptions { ApiKey = ApiKey }),
                throttle);
            var contexts = Enumerable.Range(0, 48).Select(_ => Context()).ToArray();
            using var barrier = new Barrier(contexts.Length);

            var threads = contexts
                .Select(context => new Thread(() =>
                {
                    barrier.SignalAndWait();
                    middleware.InvokeAsync(context).GetAwaiter().GetResult();
                }))
                .ToArray();
            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            contexts.Count(context => context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                .ShouldBe(FailuresPerMinute, $"round {round}: only the allowed failures may be 401");
            contexts.Count(context => context.Response.StatusCode == StatusCodes.Status429TooManyRequests)
                .ShouldBe(contexts.Length - FailuresPerMinute, $"round {round}: every further failure is 429");
        }
    }

    private static IPAddress NextAddress(int index) =>
        new(new byte[] { 10, (byte)(index >> 16), (byte)(index >> 8), (byte)index });

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
