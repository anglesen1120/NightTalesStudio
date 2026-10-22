using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

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
/// resumed cadence would upload nothing but would keep paying for stories. Before both, an archived series answers 409
/// <c>series_archived</c> (S77 review C-3/S-2): its autopilot can never run again, so a resume has nothing to release.
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
/// <b>It releases the runs, not only the series</b> (S77 review C-2/S-1; delta N-2). A run paused by
/// <c>AutopilotStepSupport.SeriesMaySpendAsync</c> is not due, so nothing else would ever look at it: this route is the
/// owner's "continue", and it hands the series' <c>owner_paused</c> runs to the one release path,
/// <see cref="AutopilotRunHold.ReleaseOwnerPausedAsync"/> — the domain's own <c>paused → previous state</c> edge, so no
/// state is guessed here, and a row whose step the ladder cannot place is skipped and counted instead of throwing.
/// A run paused for any other §3.6 reason is left to 02.8.3-T4's trigger ladder. That also covers a series whose
/// own pause was already cleared (a switch-off, or a disabled-then-enabled series): the two refusals above apply to
/// that release too, because moving a run without a live authorization would only feed it back into the spend check
/// that paused it. With no paused run left to move and no series pause, the route is the documented no-op it always
/// was.
/// </para>
/// <para>
/// <b>Idempotent.</b> Resuming a series that is not paused and holds no paused run answers <c>Success</c> without
/// touching a row (and therefore without bumping <c>xmin</c>, the autopilot's ETag), and a paused run that is already
/// back in its pre-pause state is left alone: the state is derived by <see cref="AutopilotRun.Resume"/>, so a partial
/// resume of a previous attempt is completed rather than refused.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="logger">The log of what one resume could not release (ids and counts only).</param>
public sealed partial class ResumeAutopilotHandler(
    IAppDbContext db,
    IClock clock,
    ILogger<ResumeAutopilotHandler> logger)
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

        // S77 review C-3/S-2: an archived series can never produce again, so a resume would only move run rows a
        // failing spend check stops on their next tick.
        if (series.ArchivedAt is not null)
        {
            return Error.Conflict(
                ErrorCodes.SeriesArchived,
                "The series is archived; autopilot cannot be resumed for it.");
        }

        // S77 delta N-2: the pre-check reads only `owner_paused` rows, because those are the only ones any release
        // path moves. Every other §3.6 reason belongs to 02.8.3-T4's trigger ladder, so a series holding only those
        // runs has nothing this route may do.
        var pausedRuns = await db.AutopilotRuns
            .Where(run => run.SeriesId == command.SeriesId
                && run.State == AutopilotRunState.Paused
                && run.PauseReason == AutopilotPauseReason.OwnerPaused)
            .OrderBy(run => run.SlotAt)
            .Take(AutopilotRunHold.MaxRunsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var seriesIsIdle = series.AutopilotPausedAt is null && series.AutopilotPauseReason is null;

        if (seriesIsIdle && pausedRuns.Count == 0)
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

        if (!seriesIsIdle)
        {
            // The pause's own undo comes first: the release only moves runs of a series that is not paused
            // (`AutopilotRunHold.ReleaseOwnerPausedAsync`'s guard), so the order is the series, then its runs.
            series.ResumeAutopilot(now);
        }

        // One release path (S77 delta N-2): `AutopilotRunHold` moves `owner_paused` rows only, and it skips and counts a
        // row whose step is not on the ladder instead of letting `AutopilotRun.Resume` throw out of the request. The
        // pre-check above decided the no-op; this read is the authoritative one, because the series pause was cleared
        // in between.
        var release = await AutopilotRunHold
            .ReleaseOwnerPausedAsync(db, command.SeriesId, now, cancellationToken)
            .ConfigureAwait(false);

        if (release.Refused > 0 || release.Untouched > 0)
        {
            LogRunReleaseIncomplete(command.SeriesId, release.Refused, release.Untouched);
        }

        return Result.Success;
    }

    /// <summary>Logs the owner-paused runs a resume did not move; the next resume or acceptance retries them.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="refused">How many rows the domain refused to resume (a corrupt step).</param>
    /// <param name="untouched">How many paused rows lie beyond the release bound.</param>
    [LoggerMessage(EventId = 6701, Level = LogLevel.Warning, Message = "Autopilot resume: series {seriesId} left {refused} paused run(s) unreleased and {untouched} beyond the release bound.")]
    private partial void LogRunReleaseIncomplete(Guid seriesId, int refused, int untouched);

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
