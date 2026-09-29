using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NightTales.Worker.Health;

/// <summary>
/// The Worker's health surface (09.2.6 plan §7, 09 W3 CR-42): <c>/health/live</c> is the liveness
/// probe (no checks, always 200) and <c>/health/ready</c> reports every check in its body while only
/// an <see cref="HealthStatus.Unhealthy"/> check turns it into a 503 — a degraded Hangfire storage or
/// registry is reported, never fatal (09.9-D2).
/// </summary>
/// <remarks>
/// This is deliberately minimal and bound to loopback; 09.9.3-T1 replaces it with the shared
/// observability and health surface.
/// </remarks>
public static class WorkerHealthEndpoints
{
    /// <summary>The liveness path of the Worker.</summary>
    public const string LivePath = "/health/live";

    /// <summary>The readiness path of the Worker.</summary>
    public const string ReadyPath = "/health/ready";

    /// <summary>Maps both probes onto <paramref name="endpoints"/>.</summary>
    public static IEndpointRouteBuilder MapWorkerHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false });

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = WriteReportAsync,
            ResultStatusCodes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        return endpoints;
    }

    /// <summary>Writes the per-check status; the descriptions are constant and carry no credentials.</summary>
    private static Task WriteReportAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        return context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString().ToLowerInvariant(),
                    description = entry.Value.Description,
                }),
            },
            context.RequestAborted);
    }
}
