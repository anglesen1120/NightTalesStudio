using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Authorization;

/// <summary>
/// The one place that moves a series' open runs when its consent changes: to <c>awaiting_authorization</c> when the
/// authorization goes away (02.8.3-T2 plan §3.7, contract D-3), and back to the state their step implies when it comes
/// back (S77 review C-2/S-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two directions, one home.</b> Holding: the settings write revokes a covered-field change and the disable revokes
/// with <c>disabled</c> (<c>PutAutopilotHandler</c>), and <c>DELETE /series/{id}/autopilot/authorization</c> revokes
/// with <c>owner_revoked</c> (<c>RevokeAutopilotAuthorizationHandler</c>); all three need the same move on the same
/// rows, so it lives here instead of growing a second copy that a later change (S71-43) would have to find twice.
/// Releasing: <c>AutopilotStepSupport.SeriesMaySpendAsync</c> pauses a producing run with <c>owner_paused</c> when the
/// current authorization is missing or revoked (the §3.5 table has no <c>→ awaiting_authorization</c> row outside
/// <c>planned</c>, so T1 pauses instead of guessing). Those runs are otherwise stranded: a <c>paused</c> row is not
/// due, so neither the tick nor <see cref="Orchestration.AutopilotRunOrchestrator.AdvanceAsync"/> looks at it again.
/// The acceptance is the event that restores the consent, so it is the event that releases them.
/// </para>
/// <para>
/// <b>Why not the orchestrator.</b> <c>IAutopilotRunOrchestrator.AdvanceAsync</c> executes the run's next step,
/// including the paid story and render dispatches, and it is registered only in the Worker host — calling it from a
/// request path would both fail to resolve and spend money synchronously inside an HTTP call. The state moves are
/// domain operations, so they are done here instead (contract D-3).
/// </para>
/// <para>
/// <b>Why only <c>planned</c> is held.</b> A run that is already producing is stopped by the next spend check
/// (<c>AutopilotStepSupport.SeriesMaySpendAsync</c>) — the S71-43 deferred decision — and the transition table has no
/// row from most other states to <c>awaiting_authorization</c>; touching them would either throw or skip the step's
/// own re-check. A move the state machine refuses is skipped, never a reason to fail the owner's write.
/// </para>
/// <para>
/// <b>Releasing keeps the aggregates' own rules.</b> A run goes back through <see cref="AutopilotRun.Resume"/>, which
/// derives the state from the step the row carries (<c>paused → previous state</c>, plan §3.5), so nothing here guesses
/// a transition the domain does not already have. Only runs paused with <see cref="AutopilotPauseReason.OwnerPaused"/>
/// are released: the other §3.6 reasons belong to 02.8.3-T4's trigger ladder, which is also the task that owns the
/// resume rules and the <c>autopilot.resumed</c> audit row.
/// </para>
/// </remarks>
internal static class AutopilotRunHold
{
    /// <summary>
    /// The widest plan one accepted settings document can produce, as an upper bound: the cadence's weekdays (at most
    /// seven) inside <c>AutopilotSettingsValidator.HorizonDays</c>' maximum of 14 days, plus the one slot the
    /// publish-time edge of the last day can add. <c>daily_cap</c> is not a multiplier here — plan §3.2 makes it a
    /// channel-wide cap of slots per local calendar day (<c>SlotCalculator.ApplyDailyCapToPlannedSlots</c>, one
    /// candidate per series per day), so it can only ever remove a series' slots.
    /// </summary>
    internal const int MaxPlannedRunsPerSeries = 15;

    /// <summary>
    /// How many runs one pass of a hold, a release or an advance reads: well above
    /// <see cref="MaxPlannedRunsPerSeries"/>, so a plan the validator accepted is served in one pass and the bound is
    /// defence in depth (a plan left by an older horizon, or a hand-written row). A pass that still finds more reports
    /// the count it did not read rather than dropping it silently.
    /// </summary>
    internal const int MaxRunsPerPass = 100;

    /// <summary>
    /// Moves the series' <see cref="AutopilotRunState.Planned"/> runs to
    /// <see cref="AutopilotRunState.AwaitingAuthorization"/> in the caller's transaction and touches no other state.
    /// </summary>
    /// <param name="db">The tracked write model the runs are read from and advanced in.</param>
    /// <param name="seriesId">The series whose planned runs are held.</param>
    /// <param name="now">The clock reading stamped on every moved run.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// How many runs moved, how many the state machine refused, and how many planned runs the pass did not read
    /// (a lower bound: at least that many rows lie beyond <see cref="MaxRunsPerPass"/>). A refusal is retried by the
    /// next tick; an untouched row is held by the next hold of the same series.
    /// </returns>
    internal static async Task<AutopilotRunPassResult> HoldPlannedAsync(
        IAppDbContext db,
        Guid seriesId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // One row beyond the bound tells the caller that more are waiting, without a second COUNT round trip.
        var planned = await db.AutopilotRuns
            .Where(run => run.SeriesId == seriesId
                && run.FinishedAt == null
                && run.State == AutopilotRunState.Planned)
            .OrderBy(run => run.SlotAt)
            .Take(MaxRunsPerPass + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var untouched = planned.Count > MaxRunsPerPass ? planned.Count - MaxRunsPerPass : 0;
        var held = 0;
        var refused = 0;

        foreach (var run in planned.Take(MaxRunsPerPass))
        {
            if (!AutopilotRunStateMachine.CanTransition(run.State, AutopilotRunState.AwaitingAuthorization))
            {
                refused++;
                continue;
            }

            // The last completed step is carried over unchanged: this move is not a completion, and the awaiting step
            // re-checks the authorization on every tick and resumes from what the run already did.
            run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, run.Step, now, authorizationId: null);
            held++;
        }

        return new AutopilotRunPassResult(held, refused, untouched);
    }

    /// <summary>
    /// Releases the series' runs that a missing authorization paused, when the consent is back: the series must be
    /// alive, switched on and not paused itself, and each run must be <c>paused</c> with
    /// <see cref="AutopilotPauseReason.OwnerPaused"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The caller commits.</b> This writes through the tracked model the caller owns and saves nothing, exactly like
    /// <see cref="HoldPlannedAsync"/>: a request handler leaves the commit to its pipeline, and the outbox handler that
    /// calls this saves before it advances a run in its own scope (otherwise the advance would read the old state).
    /// </para>
    /// <para>
    /// <b>The guard is the point.</b> Releasing a run of a series that is still switched off or paused would only make
    /// the next spend check pause it again, and a run of an archived series may never produce anything
    /// (<c>SeriesMaySpendAsync</c> fails it). So a series that is not <c>enabled</c>, is paused, or is archived
    /// releases nothing and answers a zero result.
    /// </para>
    /// </remarks>
    /// <param name="db">The tracked write model; the tenant filter is the caller's.</param>
    /// <param name="seriesId">The series whose runs are released.</param>
    /// <param name="now">The clock reading stamped on every resumed run.</param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>How many runs resumed, how many were skipped, and how many the pass did not read.</returns>
    internal static async Task<AutopilotRunPassResult> ReleaseOwnerPausedAsync(
        IAppDbContext db,
        Guid seriesId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == seriesId, ct)
            .ConfigureAwait(false);

        if (series is null
            || series.ArchivedAt is not null
            || !series.AutopilotEnabled
            || series.AutopilotPausedAt is not null
            || series.AutopilotPauseReason is not null)
        {
            return default;
        }

        var paused = await db.AutopilotRuns
            .Where(run => run.SeriesId == seriesId
                && run.FinishedAt == null
                && run.State == AutopilotRunState.Paused
                && run.PauseReason == AutopilotPauseReason.OwnerPaused)
            .OrderBy(run => run.SlotAt)
            .Take(MaxRunsPerPass + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var untouched = paused.Count > MaxRunsPerPass ? paused.Count - MaxRunsPerPass : 0;
        var released = 0;
        var refused = 0;

        foreach (var run in paused.Take(MaxRunsPerPass))
        {
            // `Resume` derives the state from the step and throws on a row whose step is not on the ladder; a corrupt
            // row is skipped and counted instead of failing the acceptance that triggered the release. A null step is
            // NOT corrupt: `StateBeforePause` maps it (like `authorization`) to `planned`, which is where a run that
            // has not started its first step goes back to.
            if (run.Step is not null && !AutopilotRunStep.IsKnown(run.Step))
            {
                refused++;
                continue;
            }

            run.Resume(now);
            released++;
        }

        return new AutopilotRunPassResult(released, refused, untouched);
    }
}

/// <summary>
/// What one hold or release pass changed on a series' runs.
/// </summary>
/// <param name="Changed">How many runs moved (held, or released).</param>
/// <param name="Refused">How many rows the domain refused to move (a corrupt step, or a transition the table lacks).</param>
/// <param name="Untouched">
/// How many rows the pass did not read because they lie beyond
/// <see cref="AutopilotRunHold.MaxRunsPerPass"/>; a lower bound, and zero whenever the pass read every candidate.
/// </param>
internal readonly record struct AutopilotRunPassResult(int Changed, int Refused, int Untouched);
