using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Episodes.Commands.MarkEpisodeAssetsForRetention;

/// <summary>
/// <c>UPDATE assets SET retention_until = LEAST(COALESCE(retention_until, @now), @now)
/// WHERE episode_id = @id AND deleted_at IS NULL</c> (09.5.1 plan §7): one set-based statement, so the
/// handler never loads the episode's files.
/// </summary>
/// <remarks>
/// <para>
/// <c>LEAST</c> is what makes a redelivery harmless: an asset that already has an earlier
/// <c>retention_until</c> keeps it, one with a later one moves to <paramref name="now"/>, and one with
/// none gets it. An episode without assets touches zero rows and still succeeds (09.5.1 plan §Failure
/// handling).
/// </para>
/// <para>
/// <c>ExecuteUpdateAsync</c> bypasses <c>AuditableSaveChangesInterceptor</c>, so <c>updated_at</c> is set
/// explicitly (09 W3 CR-38). The tenant filter of 09 README D10 still applies to the statement, and the
/// outbox delivery pinned the scope to the event's user before this handler was built.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the statement runs in the unit of work's transaction.</param>
/// <param name="clock">The command's clock reading; the same instant for every row.</param>
public sealed class MarkEpisodeAssetsForRetentionHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<MarkEpisodeAssetsForRetentionCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        MarkEpisodeAssetsForRetentionCommand command,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await db.Assets
            .Where(asset => asset.EpisodeId == command.EpisodeId && asset.DeletedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        asset => asset.RetentionUntil,
                        asset => asset.RetentionUntil == null || asset.RetentionUntil > now ? now : asset.RetentionUntil)
                    .SetProperty(asset => asset.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return Result.Success;
    }
}
