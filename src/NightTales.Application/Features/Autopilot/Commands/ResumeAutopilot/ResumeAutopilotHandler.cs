using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

// `NightTales.Domain.Series` is the namespace of the run and the name of the series aggregate; the autopilot feature
// aliases the aggregate (the `AutopilotSlotPlanner` convention).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Commands.ResumeAutopilot;

/// <summary>
/// <c>POST /api/v1/series/{id}/autopilot/resume</c> (02.8.3 plan §4 UC6, §3.6 "Resume"): clears the series' pause and
/// resumes each run the pause held.
/// </summary>
/// <remarks>
/// <para>
/// <b>What T2 checks.</b> The two refusals its task lists: a series with no live authorization answers 409
/// <c>autopilot_authorization_required</c> (the same filtered pointer read the paid steps use,
/// <see cref="AutopilotStepSupport.CurrentAuthorizationAsync"/>, so there is one implementation of the §3.7 rule), and
/// an authorization whose channel connection is not <c>active</c> answers 409 <c>channel_reauth_required</c> — a
/// resumed cadence would upload nothing but would keep paying for stories.
/// </para>
/// <para>
/// <b>The trigger ladder is 02.8.3-T4's.</b> Plan §3.6 "Resume" also requires every row of the trigger table to be
/// re-evaluated, and a reason that still holds must refuse with 409 <c>autopilot_pause_reason_active</c> (§10 open
/// item). That ladder reads the gate-failure streak, the budget guard, the platform spend windows, the channel's
/// incident state and 06's publication rows, and it belongs to the task that owns the triggers — so this handler
/// carries the seam and no invented behaviour: T4 replaces the single marked block below with its ladder, and nothing
/// in T2's contract changes.
/// </para>
/// <para>
/// <b>Idempotent.</b> Resuming a series that is not paused answers <c>Success</c> without touching the row (and
/// therefore without bumping <c>xmin</c>, the autopilot's ETag), and a paused run that is already back in its
/// pre-pause state is left alone: the state is derived by <see cref="AutopilotRun.Resume"/>, so a partial resume of a
/// previous attempt is completed rather than refused.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class ResumeAutopilotHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<ResumeAutopilotCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        ResumeAutopilotCommand command,
        CancellationToken cancellationToken)
    {
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.SeriesId, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            return Errors.NotFound();
        }

        if (series.AutopilotPausedAt is null && series.AutopilotPauseReason is null)
        {
            // Nothing to resume: the second click of a resume, or a series that was never paused. No trigger is
            // re-checked on a no-op, because there is no pause for a trigger to still hold.
            return Result.Success;
        }

        var now = clock.UtcNow;

        // 02.8.3-T4 SEAM — the §3.6 trigger ladder runs here: every row of the table is re-evaluated and a reason
        // that still holds answers 409 `autopilot_pause_reason_active` (plan §3.6 "Resume", §10 open item) before
        // the two checks below and before any write. T2 ships no behaviour for it (T4 owns the triggers).

        var authorizationId = await AutopilotStepSupport
            .CurrentAuthorizationAsync(db, series, cancellationToken)
            .ConfigureAwait(false);

        if (authorizationId is null)
        {
            return Error.Conflict(
                ErrorCodes.AutopilotAuthorizationRequired,
                "The series needs a current authorization before autopilot can be resumed.");
        }

        var channelActive = await AuthorizationChannelIsActiveAsync(
            authorizationId.Value,
            cancellationToken).ConfigureAwait(false);

        if (!channelActive)
        {
            return Error.Conflict(
                ErrorCodes.ChannelReauthRequired,
                "The authorization's channel connection is not active; reconnect the channel first.");
        }

        series.ResumeAutopilot(now);

        var pausedRuns = await db.AutopilotRuns
            .Where(run => run.SeriesId == command.SeriesId && run.State == AutopilotRunState.Paused)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var run in pausedRuns)
        {
            run.Resume(now);
        }

        return Result.Success;
    }

    /// <summary>
    /// Whether the channel connection the authorization snapshot names is still <c>active</c> (plan §3.6 rows 6/6b's
    /// read, without the pause: an unsupported platform is T4's trigger, and the enable route's own gate).
    /// </summary>
    /// <param name="authorizationId">The series' current authorization.</param>
    /// <param name="ct">Cancels the two reads.</param>
    /// <returns><see langword="true"/> when the connection exists and is active.</returns>
    private async Task<bool> AuthorizationChannelIsActiveAsync(Guid authorizationId, CancellationToken ct)
    {
        var connectionId = await db.SeriesAutopilotAuthorizations
            .AsNoTracking()
            .Where(row => row.Id == authorizationId)
            .Select(row => (Guid?)row.ChannelConnectionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (connectionId is not { } connection)
        {
            return false;
        }

        return await db.ChannelConnections
            .AsNoTracking()
            .AnyAsync(row => row.Id == connection && row.Status == ChannelConnectionStatus.Active, ct)
            .ConfigureAwait(false);
    }
}
