using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;

namespace NightTales.Api.Health;

/// <summary>
/// The readiness probe's database check (09 README BD12, 09.9.3-T1): <c>Database.CanConnectAsync</c> bounded
/// by <c>HealthChecks:DbTimeoutSeconds</c>. It is the only check that can turn <c>/health/ready</c> into a
/// 503 (09.9-D2); the body carries a constant, never the exception text.
/// </summary>
/// <param name="database">The scoped write model; the probe only opens a connection.</param>
/// <param name="configuration">Supplies <c>HealthChecks:DbTimeoutSeconds</c> (default 5).</param>
public sealed class AppDbContextHealthCheck(IAppDbContext database, IConfiguration configuration) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "db";

    /// <summary>The constant description of an unreachable database.</summary>
    public const string Unreachable = "database unreachable";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var seconds = Math.Max(1, configuration.GetValue("HealthChecks:DbTimeoutSeconds", 5));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        try
        {
            return await database.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy("database reachable")
                : HealthCheckResult.Unhealthy(Unreachable);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(Unreachable);
        }
    }
}

/// <summary>
/// The readiness probe's blob-store check (09 README BD12/BD16): it reads the cache the background prober
/// keeps fresh and never queries S3, so a load-balancer probe cannot become an S3 request storm.
/// </summary>
/// <param name="settings">The storage settings; the probe interval sizes the staleness window.</param>
/// <param name="cache">The cache the prober publishes to.</param>
/// <param name="clock">The host clock.</param>
public sealed class BlobStoreHealthCheck(
    IOptions<StorageSettings> settings,
    IStorageHealthCache cache,
    IClock clock) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "storage";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = cache.Current;
        var window = TimeSpan.FromSeconds(3 * Math.Max(1, settings.Value.HealthProbeSeconds));

        if (clock.UtcNow - health.CheckedAt > window)
        {
            return Task.FromResult(HealthCheckResult.Degraded("storage health stale"));
        }

        return Task.FromResult(health.IsHealthy
            ? HealthCheckResult.Healthy("storage ready")
            : HealthCheckResult.Degraded("storage unavailable"));
    }
}
