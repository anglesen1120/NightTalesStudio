using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NightTales.Api.Health;

/// <summary>
/// The Api's orchestrator probes (09 README BD12, 09.9.3-T1): <c>GET /health/live</c> touches no dependency
/// and is always 200; <c>GET /health/ready</c> is anonymous, never cached by the client, and answers 503 only
/// when the database is unreachable (09.9-D2) — a degraded blob store or render-node cache is reported with
/// 200. The database result is shared for a few seconds behind one in-flight probe.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>The liveness path.</summary>
    public const string LivePath = "/health/live";

    /// <summary>The readiness path.</summary>
    public const string ReadyPath = "/health/ready";

    /// <summary>Registers the readiness checks.</summary>
    /// <param name="services">The composition root's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddApiHealth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<NodeRegistryReadiness>();
        services.AddHealthChecks()
            .AddCheck<AppDbContextHealthCheck>(
                AppDbContextHealthCheck.Name, HealthStatus.Unhealthy, tags: ["ready", "db"])
            .AddCheck<BlobStoreHealthCheck>(
                BlobStoreHealthCheck.Name, HealthStatus.Degraded, tags: ["ready", "storage"])
            .AddCheck<NodeRegistryHealthCheck>(
                NodeRegistryHealthCheck.Name, HealthStatus.Degraded, tags: ["ready", "nodes"]);

        return services;
    }

    /// <summary>Maps both probes.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Anonymous callers pass (the fallback policy only rejects the enrolment-only token, WT34), but
        // AllowAnonymous would also bypass that rejection, so neither probe carries it.
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false });

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteReportAsync,
            ResultStatusCodes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            AllowCachingResponses = false,
        });

        return endpoints;
    }

    private static Task WriteReportAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString().ToLowerInvariant(),
                    durationMs = (long)entry.Value.Duration.TotalMilliseconds,
                }),
            },
            context.RequestAborted);
}
