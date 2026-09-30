using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;

namespace NightTales.Worker.Health;

/// <summary>
/// The readiness probe's database check (09.2.6 plan §7, 09.9-D2): one round trip through the same
/// <see cref="IAppDbContext"/> the Worker uses, so the probe fails exactly when the platform database
/// is unreachable. It is the only check that can turn <c>/health/ready</c> into a 503.
/// </summary>
/// <param name="database">The scoped write model; the probe opens its connection for one statement.</param>
/// <param name="logger">Receives the failure detail, which never reaches the response body.</param>
public sealed partial class DatabaseHealthCheck(
    IAppDbContext database,
    ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    /// <summary>The constant, credential-free description of a failing probe.</summary>
    public const string Unreachable = "database unreachable";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);

            return HealthCheckResult.Healthy("database reachable");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnreachable(exception);

            // The probe is unauthenticated: the body carries a constant, the detail goes to the log.
            return HealthCheckResult.Unhealthy(Unreachable);
        }
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Error, Message = "health.database.unreachable")]
    private partial void LogUnreachable(Exception exception);
}
