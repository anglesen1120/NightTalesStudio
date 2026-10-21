using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Rendering;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;

namespace NightTales.Api.Health;

/// <summary>
/// A boolean probe result shared for a short time and computed by at most one probe at a time (09.9.3-T1):
/// the anonymous readiness route would otherwise open one database connection per call, so a flood of probes
/// could drain the connection pool. Callers that arrive while a probe runs wait for that probe; callers that
/// arrive within the TTL of the last result get it without a probe.
/// </summary>
/// <param name="ttl">How long a finished result answers later callers.</param>
public sealed class ProbeResultCache(TimeSpan ttl)
{
    private readonly Lock _gate = new();
    private Task<bool>? _inFlight;
    private bool _result;
    private long _validUntil;

    /// <summary>The probe result, from the cache, a running probe, or a new probe.</summary>
    /// <param name="probe">The probe; it must carry its own timeout and never throw to signal failure.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared probe.</param>
    /// <returns><see langword="true"/> when the probed dependency is healthy.</returns>
    public Task<bool> GetAsync(Func<Task<bool>> probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        Task<bool> task;
        lock (_gate)
        {
            if (_inFlight is null)
            {
                if (_validUntil != 0 && Stopwatch.GetTimestamp() < _validUntil)
                {
                    return Task.FromResult(_result);
                }

                // Detached from this lock and this caller: the probe completes on its own thread and
                // stores its result under the same lock, which this method has released by then.
                _inFlight = Task.Run(() => RunAsync(probe), CancellationToken.None);
            }

            task = _inFlight;
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<bool> RunAsync(Func<Task<bool>> probe)
    {
        bool result;
        try
        {
            result = await probe().ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = false;
        }

        lock (_gate)
        {
            _result = result;
            _validUntil = Stopwatch.GetTimestamp() + (long)(ttl.TotalSeconds * Stopwatch.Frequency);
            _inFlight = null;
        }

        return result;
    }
}

/// <summary>
/// The database probe behind <c>/health/ready</c>: <c>Database.CanConnectAsync</c> in its own scope, bounded
/// by <c>HealthChecks:DbTimeoutSeconds</c>, and shared for <see cref="ResultTtl"/> through a
/// <see cref="ProbeResultCache"/>.
/// </summary>
/// <param name="scopes">Creates the probe's own scope, so it outlives the request that started it.</param>
/// <param name="configuration">Supplies <c>HealthChecks:DbTimeoutSeconds</c> (default 5).</param>
public sealed class DatabaseReadiness(IServiceScopeFactory scopes, IConfiguration configuration)
{
    /// <summary>How long a probe result answers later callers.</summary>
    public static readonly TimeSpan ResultTtl = TimeSpan.FromSeconds(3);

    private readonly ProbeResultCache _cache = new(ResultTtl);

    /// <summary>Whether the database answered the last probe inside the timeout.</summary>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <returns><see langword="true"/> when the database is reachable.</returns>
    public Task<bool> IsReachableAsync(CancellationToken cancellationToken) =>
        _cache.GetAsync(ProbeAsync, cancellationToken);

    private async Task<bool> ProbeAsync()
    {
        var seconds = Math.Max(1, configuration.GetValue("HealthChecks:DbTimeoutSeconds", 5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

            return await database.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// The readiness probe's database check (09 README BD12, 09.9.3-T1). It is the only check that can turn
/// <c>/health/ready</c> into a 503 (09.9-D2); the body carries a constant, never the exception text.
/// </summary>
/// <param name="readiness">The cached, single-flight database probe.</param>
public sealed class AppDbContextHealthCheck(DatabaseReadiness readiness) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "db";

    /// <summary>The constant description of an unreachable database.</summary>
    public const string Unreachable = "database unreachable";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await readiness.IsReachableAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy("database reachable")
            : HealthCheckResult.Unhealthy(Unreachable);
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

/// <summary>
/// Whether the <c>worker_nodes</c> registry has an <c>active</c> node (09.9.3-T1): the Worker's health poller
/// maintains <c>state</c> (11 D13), and the Api cannot read the Worker's in-memory cache, so it reads the
/// registry. The row query carries no tenant predicate (<c>WorkerNode</c> is not user-owned) and is shared
/// through a <see cref="ProbeResultCache"/>, so a probe fleet costs one query per window.
/// </summary>
/// <param name="scopes">Creates the probe's own scope.</param>
/// <param name="configuration">Supplies <c>HealthChecks:DbTimeoutSeconds</c> (default 5).</param>
public sealed class NodeRegistryReadiness(IServiceScopeFactory scopes, IConfiguration configuration)
{
    private readonly ProbeResultCache _cache = new(DatabaseReadiness.ResultTtl);

    /// <summary>Whether at least one registered node is <c>active</c>.</summary>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <returns><see langword="false"/> when none is active or the registry cannot be read.</returns>
    public Task<bool> HasActiveNodeAsync(CancellationToken cancellationToken) =>
        _cache.GetAsync(ProbeAsync, cancellationToken);

    private async Task<bool> ProbeAsync()
    {
        var seconds = Math.Max(1, configuration.GetValue("HealthChecks:DbTimeoutSeconds", 5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var readDb = scope.ServiceProvider.GetRequiredService<IReadDb>();

            return await readDb.Query<WorkerNode>()
                .AnyAsync(node => node.State == WorkerNodeState.Active, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// The readiness probe's render-node check (09 README BD12/BD16, 09.9.3-T1): healthy when the registry has an
/// active node, degraded otherwise — never unhealthy (09.9-D2), so the probe stays 200.
/// </summary>
/// <param name="readiness">The cached registry probe.</param>
public sealed class NodeRegistryHealthCheck(NodeRegistryReadiness readiness) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "nodes";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await readiness.HasActiveNodeAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy("render node available")
            : HealthCheckResult.Degraded("no active render node");
}
