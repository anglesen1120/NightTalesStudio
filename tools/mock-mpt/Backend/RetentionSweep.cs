using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Backend;

/// <summary>
/// The mock's retention sweep (11.1.2-T1 review fix MOCK-PERF-01): <c>DELETE</c> only marks <c>cleaned_at</c>
/// on the core's in-memory store, so without a sweep every cleaned tombstone stays for the process lifetime.
/// This hosted service calls the core's own <see cref="ITaskStore.PurgeCleanedAsync"/> after the contract
/// window (7 days, 11.3.5-T2) — the core is unchanged, the policy lives here.
/// </summary>
/// <remarks>
/// One sweep per <see cref="MockMptOptions.RetentionSweepInterval"/> (10 minutes by default); the purge removes
/// rows cleaned before <c>now − CleanedRetention</c>, so a fresh delete still answers idempotently for the
/// window. A failed sweep is logged by exception type and never kills the host.
/// </remarks>
public sealed partial class RetentionSweep : BackgroundService
{
    private readonly ITaskStore _store;
    private readonly MockMptOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionSweep> _logger;

    public RetentionSweep(
        ITaskStore store,
        MockMptOptions options,
        TimeProvider clock,
        ILogger<RetentionSweep> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RetentionSweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var purged = await _store
                    .PurgeCleanedAsync(_clock.GetUtcNow() - _options.CleanedRetention, stoppingToken)
                    .ConfigureAwait(false);
                if (purged > 0)
                {
                    LogPurged(purged);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A mock must survive one bad sweep; the log names the failure type, never a row.
                LogSweepFailed(exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Information,
        Message = "mock-mpt.retention: purged={Purged}")]
    private partial void LogPurged(int purged);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Warning,
        Message = "mock-mpt.retention.failed: error={ErrorType}")]
    private partial void LogSweepFailed(string errorType);
}
