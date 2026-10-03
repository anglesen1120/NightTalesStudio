using NightTales.Api.Health;
using NightTales.Api.IntegrationTests.Harness;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Health;

/// <summary>
/// The shared, single-flight readiness probe (09.9.3-T1 SEC-4): a flood of probes costs one dependency
/// call per TTL window, never one per request.
/// </summary>
public sealed class ProbeResultCacheTests
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task Concurrent_callers_share_one_in_flight_probe()
    {
        var cache = new ProbeResultCache(TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<bool> Probe()
        {
            Interlocked.Increment(ref calls);

            return gate.Task.ContinueWith(_ => true, TaskScheduler.Default);
        }

        var callers = Enumerable.Range(0, 20).Select(_ => cache.GetAsync(Probe, Ct)).ToArray();
        gate.SetResult();

        (await Task.WhenAll(callers)).ShouldAllBe(result => result);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_result_inside_the_ttl_answers_without_probing_again()
    {
        var cache = new ProbeResultCache(TimeSpan.FromSeconds(30));
        var calls = 0;

        Task<bool> Probe()
        {
            calls++;

            return Task.FromResult(false);
        }

        (await cache.GetAsync(Probe, Ct)).ShouldBeFalse();
        (await cache.GetAsync(Probe, Ct)).ShouldBeFalse();
        (await cache.GetAsync(Probe, Ct)).ShouldBeFalse();

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_result_is_probed_again()
    {
        var cache = new ProbeResultCache(TimeSpan.FromMilliseconds(20));
        var calls = 0;

        Task<bool> Probe()
        {
            calls++;

            return Task.FromResult(true);
        }

        await cache.GetAsync(Probe, Ct);
        await Task.Delay(150, Ct);
        await cache.GetAsync(Probe, Ct);

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_throwing_probe_reads_as_unhealthy_and_does_not_poison_the_cache_forever()
    {
        var cache = new ProbeResultCache(TimeSpan.FromMilliseconds(20));
        var attempt = 0;

        Task<bool> Probe() => ++attempt == 1
            ? throw new InvalidOperationException("connection refused")
            : Task.FromResult(true);

        (await cache.GetAsync(Probe, Ct)).ShouldBeFalse();
        await Task.Delay(150, Ct);
        (await cache.GetAsync(Probe, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_cancel_the_shared_probe()
    {
        var cache = new ProbeResultCache(TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var impatient = new CancellationTokenSource();

        Task<bool> Probe() => gate.Task.ContinueWith(_ => true, TaskScheduler.Default);

        var cancelled = cache.GetAsync(Probe, impatient.Token);
        var patient = cache.GetAsync(Probe, Ct);
        await impatient.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();

        (await patient).ShouldBeTrue();
    }
}
