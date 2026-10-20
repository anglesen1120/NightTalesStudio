using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Autopilot.Jobs;

namespace NightTales.Worker.Jobs;

/// <summary>
/// Registers <see cref="AutopilotPlanJob"/> as 02.8.3's single tick (plan §7.1, D17): id
/// <see cref="RecurringId"/>, every fifteen minutes, through the Worker's recurring-job reconciliation
/// (09 README D12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Flag-gated at registration, fail closed.</b> The job exists only while <c>youtube_publish</c> is on; with
/// the flag off this registration calls <see cref="IJobScheduler.RemoveRecurringIfExists"/> instead of upserting,
/// exactly like <c>DataDeletionRegistration</c> does for its switch — the rollback recipe of the plan's failure
/// table ("unregister the recurring job"). The flag lives in the database behind the cached
/// <see cref="IFeatureFlags"/> port, which is asynchronous, while
/// <see cref="IRecurringJobRegistration.Register"/> is not: the read is awaited synchronously on the Worker's
/// start-up path, once, before any request is served. A flag read that throws is treated as "off".
/// </para>
/// <para>
/// <b>Flipping the flag needs a restart.</b> The reconciliation runs at start-up only, so enabling
/// <c>youtube_publish</c> later registers the job on the next Worker start (or on the registrar's next run);
/// disabling it is immediate, because <see cref="AutopilotPlanJob.RunAsync"/> re-reads the same flag every tick
/// and returns without doing work. Per-series rollout does not exist yet (06.5.2-T1 owns
/// <c>IsEnabledForAsync</c>): this registration is all-or-nothing.
/// </para>
/// </remarks>
/// <param name="scopes">Creates the scope the flag read runs in.</param>
/// <param name="logger">Diagnostics for the flag read.</param>
public sealed partial class AutopilotPlanJobRegistration(
    IServiceScopeFactory scopes,
    ILogger<AutopilotPlanJobRegistration> logger) : IRecurringJobRegistration
{
    /// <summary>The recurring job id (plan §7.1).</summary>
    public const string RecurringId = "autopilot-plan";

    /// <summary>Every fifteen minutes (D17).</summary>
    public const string Cron = "*/15 * * * *";

    /// <inheritdoc />
    public string Id => RecurringId;

    /// <inheritdoc />
    public void Register(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        if (!IsEnabled())
        {
            LogDisabled(logger);

            scheduler.RemoveRecurringIfExists(RecurringId);

            return;
        }

        scheduler.AddOrUpdateRecurring<AutopilotPlanJob>(
            RecurringId,
            Cron,
            (job, cancellationToken) => job.RunAsync(cancellationToken));
    }

    /// <summary>The flag, read once per start-up; any failure answers "off" (09.9.2, fail closed).</summary>
    private bool IsEnabled()
    {
        try
        {
            using var scope = scopes.CreateScope();

            return scope.ServiceProvider
                .GetRequiredService<IFeatureFlags>()
                .IsEnabledAsync(AutopilotPlanJob.FeatureKey, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFlagReadFailed(logger, exception);

            return false;
        }
    }

    [LoggerMessage(
        EventId = 6531,
        Level = LogLevel.Information,
        Message = "jobs.autopilot-plan.disabled")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 6532,
        Level = LogLevel.Warning,
        Message = "jobs.autopilot-plan.flag-read-failed")]
    private static partial void LogFlagReadFailed(ILogger logger, Exception exception);
}
