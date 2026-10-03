using Microsoft.Extensions.Diagnostics.HealthChecks;
using NightTales.Application.Abstractions.Rendering;

namespace NightTales.Worker.Health;

/// <summary>
/// The readiness probe's render-node check (09 README BD12/BD16, 09.9.3-T1): it reads only
/// <see cref="INodeHealthCache"/> — no HTTP to a node — and is healthy when at least one node answered its
/// last poll as <c>ok</c>. Without one it is degraded, never unhealthy (09.9-D2).
/// </summary>
/// <param name="cache">The cache the node health poller fills.</param>
public sealed class NodeRegistryHealthCheck(INodeHealthCache cache) : IHealthCheck
{
    /// <summary>The name this check reports under.</summary>
    public const string Name = "nodes";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var anyActive = cache.All().Any(snapshot => snapshot.HttpOk && snapshot.Status == "ok");

        return Task.FromResult(anyActive
            ? HealthCheckResult.Healthy("render node available")
            : HealthCheckResult.Degraded("no render node available"));
    }
}
