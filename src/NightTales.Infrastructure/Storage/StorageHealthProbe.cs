using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// Keeps the readiness marker of the blob store fresh and publishes the outcome to
/// <see cref="IStorageHealthCache"/> (09 README BD12, 11's <c>NodeHealthPoller</c>/<c>INodeHealthCache</c>
/// pattern).
/// </summary>
/// <remarks>
/// <para>
/// The marker object (<c>Storage:HealthMarkerKey</c>, a 0-byte <c>text/plain</c> object in the
/// <em>media</em> bucket — the app key cannot write <c>nt-public-&lt;env&gt;</c>, 11 D20 H9) is the
/// source of truth; this service is what keeps <c>/health/ready</c> off the request path, because
/// readiness reads the cache only and never issues an S3 <c>HEAD</c> (BD16).
/// </para>
/// <para>
/// A probe never throws and never stops the loop: an unreachable store, a timeout (5 s) or a refused
/// operation publishes <c>IsHealthy = false</c> with a closed error code, and the readiness check turns
/// that into <c>200 + Degraded</c> — never a 503 (WT38/WT15).
/// </para>
/// </remarks>
public sealed partial class StorageHealthProbe : BackgroundService
{
    /// <summary>The deadline of one probe, independent of the store's own retry policy.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IOptions<StorageSettings> _options;
    private readonly IBlobStore _blobs;
    private readonly IStorageHealthCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<StorageHealthProbe> _logger;

    /// <summary>Creates the prober.</summary>
    /// <param name="options">The storage settings (endpoint, bucket, marker key, interval).</param>
    /// <param name="blobs">The store the marker is written through.</param>
    /// <param name="cache">The cache readiness reads.</param>
    /// <param name="clock">The probe's clock reading, so tests drive it with <c>FakeClock</c>.</param>
    /// <param name="logger">Receives the failure detail; never a credential.</param>
    public StorageHealthProbe(
        IOptions<StorageSettings> options,
        IBlobStore blobs,
        IStorageHealthCache cache,
        IClock clock,
        ILogger<StorageHealthProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _blobs = blobs;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The probe interval of the bound settings.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(1, _options.Value.HealthProbeSeconds));

    /// <summary>
    /// Runs one probe and publishes its outcome. Public because the tests drive it directly instead of
    /// waiting for the loop (09 README D13: no <c>Thread.Sleep</c>).
    /// </summary>
    /// <param name="cancellationToken">Stops the host; a deadline of its own still applies.</param>
    /// <returns>The observation that was published.</returns>
    public async Task<StorageHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        var settings = _options.Value;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);

        try
        {
            var exists = await _blobs
                .ExistsAsync(settings.Buckets.Media, settings.HealthMarkerKey, deadline.Token)
                .ConfigureAwait(false);

            if (exists.IsError)
            {
                return PublishUnhealthy();
            }

            if (!exists.Value)
            {
                // Idempotent by construction: the write only happens when the marker is absent, so a
                // running store is never rewritten every 30 s.
                var written = await _blobs
                    .PutAsync(
                        settings.Buckets.Media,
                        settings.HealthMarkerKey,
                        Stream.Null,
                        "text/plain",
                        deadline.Token)
                    .ConfigureAwait(false);

                if (written.IsError)
                {
                    return PublishUnhealthy();
                }
            }

            return Publish(new StorageHealth(IsHealthy: true, _clock.UtcNow, ErrorCode: null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogProbeFailed(StorageHealth.UnavailableErrorCode);

            return PublishUnhealthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProbeFailed(exception, StorageHealth.UnavailableErrorCode);

            return PublishUnhealthy();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private StorageHealth PublishUnhealthy() =>
        Publish(new StorageHealth(IsHealthy: false, _clock.UtcNow, StorageHealth.UnavailableErrorCode));

    private StorageHealth Publish(StorageHealth health)
    {
        _cache.Publish(health);

        return health;
    }

    [LoggerMessage(
        EventId = 2120,
        Level = LogLevel.Warning,
        Message = "storage.health.probe.failed code={ErrorCode}")]
    private partial void LogProbeFailed(string errorCode);

    [LoggerMessage(
        EventId = 2121,
        Level = LogLevel.Warning,
        Message = "storage.health.probe.failed code={ErrorCode}")]
    private partial void LogProbeFailed(Exception exception, string errorCode);
}
