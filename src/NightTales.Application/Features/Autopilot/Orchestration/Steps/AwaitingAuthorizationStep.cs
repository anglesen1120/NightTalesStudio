using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// <see cref="AutopilotRunState.AwaitingAuthorization"/>: the run waits for the series to hold a current
/// authorization and goes back to <see cref="AutopilotRunState.Planned"/> once it does (02.8.3 plan §3.5 row
/// <c>awaiting_authorization → planned</c>, key <c>…:authorization</c>, plan §3.7 read-only).
/// </summary>
/// <remarks>
/// <para>
/// The wait is not a pause and has no expiry of its own: a series that never accepts its authorization text has no
/// publishing right, and the slot dies with the cadence it was planned for (the missed-slot rule then fails it in
/// the idea step). 02.8.3-T2 accepts and revokes the text; this step only reads
/// <c>series.current_autopilot_authorization_id</c> and the revision's <c>revoked_at</c>.
/// </para>
/// <para>
/// Re-entering <see cref="AutopilotRunState.Planned"/> keeps the <c>step</c> column at <c>authorization</c>: that is
/// the key the transition spent, and <c>idea</c> is still ahead of it on the ladder, so the idea step runs next.
/// The revision the run is authorized under is stamped on the run at the same time, so the notice an owner receives
/// later can name it.
/// </para>
/// </remarks>
public sealed partial class AwaitingAuthorizationStep(
    IAppDbContext db,
    ILogger<AwaitingAuthorizationStep> logger) : IAutopilotStepHandler
{
    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.AwaitingAuthorization;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        if (context.Now < run.StartAt)
        {
            return StepResult.Idle;
        }

        var authorizationId = await AutopilotStepSupport
            .CurrentAuthorizationAsync(db, context.Series, ct)
            .ConfigureAwait(false);

        if (authorizationId is null)
        {
            LogStillWaiting(run.Id, run.SeriesId);

            return StepResult.Idle;
        }

        run.AdvanceTo(AutopilotRunState.Planned, AutopilotRunStep.Authorization, context.Now, authorizationId);

        LogAuthorized(run.Id, authorizationId.Value);

        return StepResult.Advanced(AutopilotRunStep.Authorization);
    }

    [LoggerMessage(
        EventId = 6549,
        Level = LogLevel.Debug,
        Message = "autopilot_authorization_waiting runId={RunId} seriesId={SeriesId}")]
    private partial void LogStillWaiting(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6550,
        Level = LogLevel.Information,
        Message = "autopilot_authorization_accepted runId={RunId} authorizationId={AuthorizationId}")]
    private partial void LogAuthorized(Guid runId, Guid authorizationId);
}
