using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Telemetry;
using NightTales.Infrastructure.Rendering;

namespace NightTales.Worker.Rendering;

/// <summary>
/// Feeds the <c>render_jobs_active</c> gauge (09 README BD12, 09.9.3-T1): every 30 s one Worker — the one that
/// wins <c>pg_try_advisory_lock(hashtext('render_metrics'))</c> inside <see cref="RenderMetricsQuery"/> —
/// counts the in-flight render jobs and publishes the number through
/// <see cref="NightTalesMetrics.ResetRenderJobsActive"/>. A tick that loses the lock changes nothing.
/// </summary>
public sealed partial class RenderMetricsRefresher(
    RenderMetricsQuery query,
    NightTalesMetrics metrics,
    ILogger<RenderMetricsRefresher> logger) : BackgroundService
{
    /// <summary>The refresh period.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Runs one refresh; the test harness drives it directly instead of waiting for the loop.</summary>
    /// <param name="cancellationToken">The shutdown token.</param>
    /// <returns><see langword="true"/> when this Worker held the lock and published a count.</returns>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var count = await query.CountActiveAsync(cancellationToken);
        if (count is not { } active)
        {
            return false;
        }

        metrics.ResetRenderJobsActive(active);

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogRefreshFailed(exception);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // A stopped Worker no longer observes the queue: the gauge goes back to zero rather than freezing.
        metrics.ResetRenderJobsActive(0);
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Warning, Message = "metrics.render_jobs_active.refresh_failed")]
    private partial void LogRefreshFailed(Exception exception);
}
