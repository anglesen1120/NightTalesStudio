using Microsoft.Extensions.Hosting;

namespace NightTales.MptAdapter.Health;

/// <summary>
/// The only driver of health probes (D7, D19 Q1): every sampler runs once at start, then a periodic one repeats at
/// its own interval. <c>GET /v1/health</c> never reaches a probe, so its cost does not depend on how often the
/// Worker polls it. A sampler turns its own failures into a failed sample; anything else is logged by the host
/// and the loop carries on.
/// </summary>
internal sealed class HealthSamplerService(IEnumerable<IHealthSampler> samplers, TimeProvider clock)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var all = samplers.ToList();
        await Task.WhenAll(all.Select(sampler => RunAsync(sampler, stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunAsync(IHealthSampler sampler, CancellationToken stoppingToken)
    {
        await SampleOnceAsync(sampler, stoppingToken).ConfigureAwait(false);
        if (sampler.Interval is not { } interval)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SampleOnceAsync(sampler, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private static async Task SampleOnceAsync(IHealthSampler sampler, CancellationToken stoppingToken)
    {
        try
        {
            await sampler.SampleAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception)
        {
            // A sampler that throws past its own guard leaves its cache untouched: it goes stale after
            // 3 x its interval and the node reports the source's reason, so the failure is never silent.
        }
    }
}
