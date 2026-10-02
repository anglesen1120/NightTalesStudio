using System.Diagnostics;
using NightTales.Api.Http;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The WT40 timing floor of <c>POST /auth/forgot-password</c> (09.3.3-T1): the constants the contract
/// fixes and the fact that the pad really waits.
/// </summary>
/// <remarks>
/// No database and no host: this is the deterministic half of the timing rule. The end-to-end half (both
/// paths padded and the responses byte-identical) lives in <c>PasswordResetTests</c>.
/// </remarks>
public sealed class AuthTimingTests
{
    [Fact]
    public void Jitter_StaysWithinTheBound()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var jitter = AuthTiming.Jitter();

            jitter.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
            jitter.ShouldBeLessThanOrEqualTo(AuthTiming.MaxJitter);
        }
    }

    [Fact]
    public async Task PadAsync_WaitsAtLeastTheFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = Stopwatch.GetTimestamp();

        await AuthTiming.PadAsync(started, ct);

        Stopwatch.GetElapsedTime(started).ShouldBeGreaterThanOrEqualTo(AuthTiming.Floor);
    }

    [Fact]
    public async Task PadAsync_DoesNotWaitTwice_WhenTheWorkAlreadyTookLonger()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = Stopwatch.GetTimestamp();

        await Task.Delay(AuthTiming.Floor + AuthTiming.MaxJitter + TimeSpan.FromMilliseconds(50), ct);

        var beforePad = Stopwatch.GetElapsedTime(started);
        await AuthTiming.PadAsync(started, ct);
        var afterPad = Stopwatch.GetElapsedTime(started);

        // The pad tops up to the floor; it never adds a second full wait on top of slower work.
        (afterPad - beforePad).ShouldBeLessThan(TimeSpan.FromMilliseconds(100));
    }
}
