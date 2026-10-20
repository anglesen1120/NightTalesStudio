using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Planning;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Autopilot.Jobs;

/// <summary>
/// 02.8.3's single tick (plan §7.1, D17): plan every enabled series' slots, then advance the runs that are
/// due — once every fifteen minutes, id <c>autopilot-plan</c>, registered by the Worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>One flag, checked twice.</b> The registration is created only while <c>youtube_publish</c> is on, and this
/// body re-checks the same flag on every tick: turning the flag off stops the work within one tick without a
/// restart (the registration's own lifecycle still needs one, see the registration). Both reads fail closed.
/// </para>
/// <para>
/// <b>The two halves are independent.</b> A planning failure does not skip the advance half (the time-based
/// fallback of plan §7.2 must keep driving runs whose planner broke), and neither half rethrows anything but
/// cancellation: the tick is a recurring job and Hangfire's retry policy is 0, so the next tick is the retry.
/// Per-series and per-run failures are already contained inside the planner and the orchestrator.
/// </para>
/// <para>D19: the counters and ids only — never a title, a story, a brief or a source URL.</para>
/// </remarks>
[JobPolicy(Queue = JobQueue.Default, Retries = 0)]
public sealed partial class AutopilotPlanJob(
    IFeatureFlags flags,
    AutopilotSlotPlanner planner,
    IAutopilotRunOrchestrator orchestrator,
    AutopilotRunOptions options,
    IClock clock,
    ILogger<AutopilotPlanJob> logger) : IBackgroundJob
{
    /// <summary>The feature flag the whole autopilot feature sits behind (06 README, 06.5.2).</summary>
    public const string FeatureKey = "youtube_publish";

    /// <summary>Runs one tick: the flag, then planning, then the due runs.</summary>
    /// <param name="cancellationToken">Hangfire's shutdown token.</param>
    /// <returns>A task that completes when the tick is done.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (!await flags.IsEnabledAsync(FeatureKey, cancellationToken).ConfigureAwait(false))
        {
            LogDisabled(now);

            return;
        }

        var planned = 0;

        try
        {
            planned = await planner.PlanAsync(now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPlanningFailed(exception.GetType().Name);
        }

        var advanced = 0;

        try
        {
            advanced = await orchestrator
                .AdvanceDueAsync(now, options.PlanJobMaxRunsPerTick, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogAdvanceFailed(exception.GetType().Name);
        }

        LogTickCompleted(planned, advanced);
    }

    [LoggerMessage(
        EventId = 6558,
        Level = LogLevel.Debug,
        Message = "autopilot_plan_disabled at={At}")]
    private partial void LogDisabled(DateTimeOffset at);

    [LoggerMessage(
        EventId = 6528,
        Level = LogLevel.Error,
        Message = "autopilot_plan_planning_failed exceptionType={ExceptionType}")]
    private partial void LogPlanningFailed(string exceptionType);

    [LoggerMessage(
        EventId = 6529,
        Level = LogLevel.Error,
        Message = "autopilot_plan_advance_failed exceptionType={ExceptionType}")]
    private partial void LogAdvanceFailed(string exceptionType);

    [LoggerMessage(
        EventId = 6530,
        Level = LogLevel.Information,
        Message = "autopilot_plan_tick planned={Planned} advanced={Advanced}")]
    private partial void LogTickCompleted(int planned, int advanced);
}
