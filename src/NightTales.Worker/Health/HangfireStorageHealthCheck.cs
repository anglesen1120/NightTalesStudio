using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace NightTales.Worker.Health;

/// <summary>
/// The readiness probe's Hangfire storage check (09.2.6 plan §7). A storage that cannot be read is
/// reported as <see cref="HealthStatus.Degraded"/>, never as <see cref="HealthStatus.Unhealthy"/>:
/// 09.9-D2 reserves the 503 for a failing database check, because a Worker whose storage hiccups can
/// still serve traffic while its jobs wait.
/// </summary>
/// <param name="storage">The Hangfire storage registered by the Worker composition root.</param>
/// <param name="logger">Receives the failure detail, which never reaches the response body.</param>
/// <remarks>
/// One indexed lookup of the recurring-job id set proves the round trip. The statistics aggregate
/// (<c>GetMonitoringApi().GetStatistics()</c>) groups the whole job table on every probe, which a health
/// listener may not pay for.
/// </remarks>
public sealed partial class HangfireStorageHealthCheck(
    JobStorage storage,
    ILogger<HangfireStorageHealthCheck> logger) : IHealthCheck
{
    /// <summary>The constant, credential-free description of a degraded probe.</summary>
    public const string Unreachable = "hangfire storage unreachable";

    /// <summary>The set Hangfire stores the recurring-job ids in.</summary>
    private const string RecurringJobsSet = "recurring-jobs";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = storage.GetConnection();
            var recurring = connection.GetAllItemsFromSet(RecurringJobsSet).Count;

            return Task.FromResult(HealthCheckResult.Healthy(
                $"hangfire storage reachable; {recurring} recurring job(s)"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnreachable(exception);

            return Task.FromResult(HealthCheckResult.Degraded(Unreachable));
        }
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "health.hangfire.unreachable")]
    private partial void LogUnreachable(Exception exception);
}
