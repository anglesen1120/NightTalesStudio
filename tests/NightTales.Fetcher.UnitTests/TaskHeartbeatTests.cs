using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Fetcher.Heartbeat;
using NightTales.Fetcher.InternalApi;
using Shouldly;
using Xunit;

namespace NightTales.Fetcher.UnitTests;

/// <summary>02.5.2-T3 step 4: the heartbeat period, the lost lease, dispose, and the T105 bound.</summary>
public sealed class TaskHeartbeatTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly FakeApi _api = new();
    private readonly FakeTime _time = new();
    private readonly Guid _taskId = Guid.NewGuid();

    private TaskHeartbeat Start(TimeSpan? maxLifetime = null) =>
        new(_api, _taskId, "unit-worker", Interval, maxLifetime, _time, NullLogger.Instance, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Heartbeat_FiresEvery60Seconds()
    {
        using var heartbeat = Start();
        await Wait.UntilAsync(() => _time.PendingTimers >= 1, "the heartbeat timer");

        _time.Advance(TimeSpan.FromSeconds(59));
        await Wait.SettleAsync();
        _api.Heartbeats.ShouldBe(0);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => _api.Heartbeats == 1, "the first heartbeat at 60 s");

        _time.Advance(Interval);
        await Wait.UntilAsync(() => _api.Heartbeats == 2, "the second heartbeat at 120 s");
        heartbeat.Token.IsCancellationRequested.ShouldBeFalse();
        heartbeat.Reason.ShouldBe(HeartbeatStop.None);
    }

    [Fact]
    public async Task Heartbeat_OnLeaseLost_SetsCancellation()
    {
        _api.OnHeartbeat = _ => FetcherApiErrors.LeaseLost;
        using var heartbeat = Start();
        await Wait.UntilAsync(() => _time.PendingTimers >= 1, "the heartbeat timer");

        _time.Advance(Interval);

        await Wait.UntilAsync(() => heartbeat.Token.IsCancellationRequested, "the lease-lost cancellation");
        heartbeat.Reason.ShouldBe(HeartbeatStop.LeaseLost);

        _time.Advance(Interval);
        await Wait.SettleAsync();
        _api.Heartbeats.ShouldBe(1, "a lost lease stops the heartbeat");
    }

    [Fact]
    public async Task Heartbeat_StopsOnDispose()
    {
        var heartbeat = Start();
        await Wait.UntilAsync(() => _time.PendingTimers >= 1, "the heartbeat timer");

        heartbeat.Dispose();
        _time.Advance(Interval);
        _time.Advance(Interval);
        await Wait.SettleAsync();

        _api.Heartbeats.ShouldBe(0);
    }

    [Fact]
    public async Task Heartbeat_FetchSource_StopsAfterTotalPlusExtractTimeout()
    {
        // T105: TotalTimeoutSeconds (10) + ExtractTimeoutSeconds (5) from the fetcher defaults.
        var options = new FetcherOptions();
        var bound = TimeSpan.FromSeconds(options.TotalTimeoutSeconds + options.ExtractTimeoutSeconds);
        using var heartbeat = Start(bound);
        await Wait.UntilAsync(() => _time.PendingTimers >= 2, "the heartbeat and the bound timers");

        _time.Advance(bound - TimeSpan.FromSeconds(1));
        heartbeat.Token.IsCancellationRequested.ShouldBeFalse();

        _time.Advance(TimeSpan.FromSeconds(1));

        heartbeat.Token.IsCancellationRequested.ShouldBeTrue("the work is cancelled at the bound");
        heartbeat.Reason.ShouldBe(HeartbeatStop.Expired);
        _time.Advance(Interval);
        await Wait.SettleAsync();
        _api.Heartbeats.ShouldBe(0, "the heartbeat stopped itself with the bound");
    }
}
