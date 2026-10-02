using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;

namespace NightTales.Worker.Health;

/// <summary>
/// The readiness probe's storage check (09 README BD12/BD16): it reads the cache
/// <see cref="StorageHealthProbe"/> keeps fresh and never queries S3 — "health endpoints read caches
/// only", so an LB probe can never turn into an S3 request storm.
/// </summary>
/// <remarks>
/// <para>
/// It is <em>degraded</em> — never unhealthy — when the store is down or the cache has aged out
/// (3 × <c>Storage:HealthProbeSeconds</c>, i.e. the prober stopped reporting). Only the database check
/// can turn <c>/health/ready</c> into a 503 (WT38/WT15, 09.9-D2).
/// </para>
/// <para>
/// The descriptions are constants and carry no credential, bucket or key.
/// </para>
/// </remarks>
/// <param name="settings">The storage settings; the probe interval sizes the staleness window.</param>
/// <param name="cache">The cache the prober publishes to and this check only reads.</param>
/// <param name="clock">The host clock, so the staleness window is testable with <c>FakeClock</c>.</param>
public sealed class StorageHealthCheck(
    IOptions<StorageSettings> settings,
    IStorageHealthCache cache,
    IClock clock) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "storage";

    /// <summary>The constant description of a store the prober just reached.</summary>
    public const string ReadyDescription = "storage ready";

    /// <summary>The constant description of a store the prober could not reach.</summary>
    public const string UnavailableDescription = "storage unavailable";

    /// <summary>The constant description of a cache the prober stopped updating.</summary>
    public const string StaleDescription = "storage health stale";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = cache.Current;

        if (clock.UtcNow - health.CheckedAt > StalenessWindow(settings.Value))
        {
            return Task.FromResult(HealthCheckResult.Degraded(StaleDescription));
        }

        return Task.FromResult(health.IsHealthy
            ? HealthCheckResult.Healthy(ReadyDescription)
            : HealthCheckResult.Degraded(UnavailableDescription));
    }

    /// <summary>How old an observation may be before it stops counting as evidence.</summary>
    /// <param name="settings">The bound storage settings.</param>
    /// <returns>Three probe intervals, at least one second each.</returns>
    private static TimeSpan StalenessWindow(StorageSettings settings) =>
        TimeSpan.FromSeconds(3 * Math.Max(1, settings.HealthProbeSeconds));
}
