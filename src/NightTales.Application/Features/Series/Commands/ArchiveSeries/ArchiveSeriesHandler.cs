using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Series.Commands.ArchiveSeries;

/// <summary>
/// <c>DELETE /api/v1/series/{id}</c> (09.4.1 plan §4 U3): set <c>archived_at</c> so the series leaves
/// <c>GET /series</c> while its episodes stay readable (00 §6, 09 §4 09.4.1).
/// </summary>
/// <remarks>
/// The portfolio rule of the plan's failure table applies here unchanged: a caller who cannot see the
/// row gets 404, and the second archive of one's own series is a success with nothing to raise — the
/// aggregate owns that idempotence, so this handler has no branch for it.
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class ArchiveSeriesHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<ArchiveSeriesCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        ArchiveSeriesCommand command,
        CancellationToken cancellationToken)
    {
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            return Errors.NotFound();
        }

        // The pointer is read before the archive clears it (Series.Archive → DisableAutopilot), because the row it
        // names is the consent that has to be revoked in the same transaction.
        var currentAuthorizationId = series.CurrentAutopilotAuthorizationId;
        var now = clock.UtcNow;

        var archived = series.Archive(now);
        if (archived.IsError)
        {
            return archived.Errors;
        }

        if (currentAuthorizationId is { } authorizationId)
        {
            var authorization = await db.SeriesAutopilotAuthorizations
                .SingleOrDefaultAsync(candidate => candidate.Id == authorizationId, cancellationToken)
                .ConfigureAwait(false);

            // `disabled` is the plan §3.7 reason for both "the owner turned it off" and "the series is gone"; a row
            // already revoked stays as it is (Revoke answers false) and the audit summary keeps no id.
            if (authorization is not null
                && authorization.Revoke(AutopilotAuthorizationRevokeReason.Disabled, now))
            {
                command.RevokedAuthorizationId = authorization.Id;
            }
        }

        // No run hold: an archived series is disabled, and a disabled series cannot spend
        // (AutopilotStepSupport.SeriesMaySpendAsync), so the next tick retires its runs without a call from here.
        return Result.Success;
    }
}
