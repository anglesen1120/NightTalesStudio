using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Commands.RevokeAutopilotAuthorization;

/// <summary>
/// Revokes the series' live authorization with <c>owner_revoked</c> and holds its open runs (02.8.3-T2 plan §3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>No re-auth and no legal gate.</b> Taking consent back reduces risk, exactly like disabling, so the route is
/// ungated beyond authentication and ownership (plan §3.7 lists the gates on the consent-<em>bearing</em> actions
/// only).
/// </para>
/// <para>
/// <b>Autopilot is not switched off.</b> The pointer is cleared and the rows' runs are advanced once so each one
/// stops at <c>awaiting_authorization</c>; the enable flag, the settings and the pause state are untouched. That is
/// the plan's explicit rule, and it is what lets a re-accept resume the schedule without re-enabling.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The clock the revocation and the pointer clear are stamped with.</param>
public sealed class RevokeAutopilotAuthorizationHandler(
    IAppDbContext db,
    IClock clock)
    : ICommandHandler<RevokeAutopilotAuthorizationCommand, ErrorOr<Success>>
{
    /// <summary>How many open runs one request holds; the planner keeps far fewer per series.</summary>
    private const int MaxHeldRuns = 20;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        RevokeAutopilotAuthorizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            return Errors.NotFound();
        }

        if (series.CurrentAutopilotAuthorizationId is not { } currentId)
        {
            return Result.Success;
        }

        var row = await db.SeriesAutopilotAuthorizations
            .SingleOrDefaultAsync(candidate => candidate.Id == currentId, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        series.SetCurrentAutopilotAuthorization(null, now);

        if (row is null || !row.Revoke(AutopilotAuthorizationRevokeReason.OwnerRevoked, now))
        {
            // The pointer named a row that was already dead: the clear above is the whole change and there is
            // nothing to audit, which the projection answers with `null`.
            return Result.Success;
        }

        command.RevokedAuthorizationId = row.Id;
        command.RevokedVersion = row.Version;

        await HoldOpenRunsAsync(series, now, cancellationToken).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Moves the series' <see cref="AutopilotRunState.Planned"/> runs to
    /// <see cref="AutopilotRunState.AwaitingAuthorization"/> in this transaction (D-3), and touches no other state.
    /// </summary>
    /// <remarks>
    /// <c>IAutopilotRunOrchestrator.AdvanceAsync</c> runs the next step (including the paid dispatches) and is
    /// registered only in the Worker host, so a request path must not call it. A run already producing is stopped by
    /// its next spend check (<c>AutopilotStepSupport.SeriesMaySpendAsync</c>, the S71-43 deferred decision); a move the
    /// state machine refuses is skipped, never a reason to fail the revocation.
    /// </remarks>
    /// <param name="series">The series whose planned runs are held.</param>
    /// <param name="now">The clock reading stamped on every moved run.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Nothing.</returns>
    private async Task HoldOpenRunsAsync(SeriesAggregate series, DateTimeOffset now, CancellationToken ct)
    {
        var planned = await db.AutopilotRuns
            .Where(run => run.SeriesId == series.Id
                && run.FinishedAt == null
                && run.State == AutopilotRunState.Planned)
            .OrderBy(run => run.SlotAt)
            .Take(MaxHeldRuns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var run in planned)
        {
            if (!AutopilotRunStateMachine.CanTransition(run.State, AutopilotRunState.AwaitingAuthorization))
            {
                continue;
            }

            run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, run.Step, now, authorizationId: null);
        }
    }
}
