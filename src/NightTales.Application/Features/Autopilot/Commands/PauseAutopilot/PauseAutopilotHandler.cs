using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;

// `NightTales.Domain.Series` is the namespace of the run and the name of the series aggregate; the autopilot feature
// aliases the aggregate (the `AutopilotSlotPlanner` convention).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Commands.PauseAutopilot;

/// <summary>
/// <c>POST /api/v1/series/{id}/autopilot/pause</c> (02.8.3 plan §4 UC5, §3.6 row 15): stamps the series' pause and
/// holds every open run of it, so a run already in flight stops before its next paid dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two writes, one transaction.</b> The series row carries the state the planner reads
/// (<c>autopilot_paused_at</c>/<c>autopilot_pause_reason</c>, through
/// <see cref="SeriesAggregate.PauseAutopilot"/>) and each open run carries its own
/// <c>pause_reason</c> (through <see cref="AutopilotRun.Pause"/>). Both go through the tracked model, so the
/// pipeline's unit of work commits them together (09 README D5) and the next tick can never see a paused series with
/// a run still spending.
/// </para>
/// <para>
/// <b>Only the states the machine allows.</b> Runs that are already finished are skipped (a terminal run never moves
/// again), and a run already paused for the same reason is left untouched so a double click does not rewrite
/// <c>last_transition_at</c> of every open run. The terminal set is spelled out because an EF predicate must be
/// translatable — <c>AutopilotRunStateMachine.IsTerminal</c> answers in C#, not in SQL — exactly as
/// <c>AutopilotRunLocator</c> does it, and the exact machine check runs again in memory as the belt to that
/// braces.
/// </para>
/// <para>
/// <b>No audit here.</b> The <c>autopilot.paused</c> key of plan §4 D13 belongs to 02.8.3-T4 (task §Design: "Pause/resume
/// keys are T4's"), together with the trigger table and the alert e-mail, which is why this command writes none:
/// T4 wraps this handler's decision with its own audit entry rather than shipping a second one.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class PauseAutopilotHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<PauseAutopilotCommand, ErrorOr<Success>>
{
    /// <summary>
    /// The five terminal states of plan §3.5 — the same set as <c>AutopilotRunStateMachine.IsTerminal</c>, spelled out
    /// because a query predicate has to be translatable (the <c>AutopilotRunLocator</c> rule).
    /// </summary>
    private static readonly AutopilotRunState[] TerminalStates =
    [
        AutopilotRunState.Published,
        AutopilotRunState.PublishedPrivateLocked,
        AutopilotRunState.GateFailed,
        AutopilotRunState.CancelledByOwner,
        AutopilotRunState.Failed,
    ];

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        PauseAutopilotCommand command,
        CancellationToken cancellationToken)
    {
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.SeriesId, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            return Errors.NotFound();
        }

        var now = clock.UtcNow;

        series.PauseAutopilot(AutopilotPauseReason.OwnerPaused, now);

        var openRuns = await db.AutopilotRuns
            .Where(run => run.SeriesId == command.SeriesId && !TerminalStates.Contains(run.State))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var run in openRuns)
        {
            if (AutopilotRunStateMachine.IsTerminal(run.State))
            {
                continue;
            }

            if (run.State == AutopilotRunState.Paused && run.PauseReason == AutopilotPauseReason.OwnerPaused)
            {
                continue;
            }

            run.Pause(AutopilotPauseReason.OwnerPaused, now);
        }

        return Result.Success;
    }
}
