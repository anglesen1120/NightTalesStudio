using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;

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

        await AutopilotRunHold.HoldPlannedAsync(db, series.Id, now, cancellationToken).ConfigureAwait(false);

        return Result.Success;
    }
}
