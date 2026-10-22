using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Authorization;

/// <summary>
/// The one place that moves a series' planned runs to <c>awaiting_authorization</c> when its consent goes away
/// (02.8.3-T2 plan §3.7, contract D-3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two callers, one rule.</b> The settings write revokes a covered-field change and the disable revokes with
/// <c>disabled</c> (<c>PutAutopilotHandler</c>), and <c>DELETE /series/{id}/autopilot/authorization</c> revokes with
/// <c>owner_revoked</c> (<c>RevokeAutopilotAuthorizationHandler</c>). All three need the same move on the same rows, so
/// it lives here instead of growing a second copy that a later change (S71-43) would have to find twice.
/// </para>
/// <para>
/// <b>Why not the orchestrator.</b> <c>IAutopilotRunOrchestrator.AdvanceAsync</c> executes the run's next step,
/// including the paid story and render dispatches, and it is registered only in the Worker host — calling it from a
/// request path would both fail to resolve and spend money synchronously inside an HTTP call. The state move is a
/// domain operation, so it is done here instead.
/// </para>
/// <para>
/// <b>Why only <c>planned</c>.</b> A run that is already producing is stopped by the next spend check
/// (<c>AutopilotStepSupport.SeriesMaySpendAsync</c>) — the S71-43 deferred decision — and the transition table has no
/// row from most other states to <c>awaiting_authorization</c>; touching them would either throw or skip the step's
/// own re-check that lets a new acceptance resume the run. A move the state machine refuses is skipped, never a
/// reason to fail the owner's write.
/// </para>
/// </remarks>
internal static class AutopilotRunHold
{
    /// <summary>How many open runs one request holds; the planner keeps far fewer per series.</summary>
    internal const int MaxHeldRuns = 20;

    /// <summary>
    /// Moves the series' <see cref="AutopilotRunState.Planned"/> runs to
    /// <see cref="AutopilotRunState.AwaitingAuthorization"/> in the caller's transaction and touches no other state.
    /// </summary>
    /// <param name="db">The tracked write model the runs are read from and advanced in.</param>
    /// <param name="seriesId">The series whose planned runs are held.</param>
    /// <param name="now">The clock reading stamped on every moved run.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The number of run ids the state machine refused; the caller logs them and the next tick retries.</returns>
    internal static async Task<int> HoldPlannedAsync(
        IAppDbContext db,
        Guid seriesId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var planned = await db.AutopilotRuns
            .Where(run => run.SeriesId == seriesId
                && run.FinishedAt == null
                && run.State == AutopilotRunState.Planned)
            .OrderBy(run => run.SlotAt)
            .Take(MaxHeldRuns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var refused = 0;
        foreach (var run in planned)
        {
            if (!AutopilotRunStateMachine.CanTransition(run.State, AutopilotRunState.AwaitingAuthorization))
            {
                refused++;
                continue;
            }

            // The last completed step is carried over unchanged: this move is not a completion, and the awaiting step
            // re-checks the authorization on every tick and resumes from what the run already did.
            run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, run.Step, now, authorizationId: null);
        }

        return refused;
    }
}
