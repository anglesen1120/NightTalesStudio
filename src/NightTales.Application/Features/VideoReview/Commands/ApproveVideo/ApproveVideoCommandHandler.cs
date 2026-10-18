using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.VideoReview.Commands.ApproveVideo;

/// <summary>
/// Moves one reviewed episode to <c>ready</c> behind the episode row lock (09.7 plan §4 UC1, D5): the load,
/// the state check, the <c>If-Match</c> check, the transition and the asset re-stamp run in one transaction,
/// so a concurrent approve or reject cannot interleave with them.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract. The tenant-filtered load answers 404 for an unknown episode and for another
/// user's (D10, never 403); the <em>state</em> check runs before the <c>If-Match</c> comparison, because a
/// client retry after the state moved must see the 409 (<c>episode_invalid_state</c>) rather than a stale-tag
/// 412 — the <c>ReopenStoryCommandHandler</c> precedent; a null <c>If-Match</c> skips only the comparison, so
/// an in-process caller cannot approve a video that is no longer under review.
/// </para>
/// <para>
/// The accepted cut re-stamps every live asset of the episode for the approved window (WT56): the ingest
/// stamped the unapproved one, and this is the write that replaces it. The domain method alone owns the status
/// change and the <c>VideoApproved</c> event; this handler adds no second guard for a missing
/// <c>final_video_asset_id</c> (that invariant is 09.6.3-T2's) and calls no <c>SaveChanges</c>, opens no
/// transaction and sends nothing — <c>UnitOfWorkBehavior</c> commits the aggregate, the audit row and the
/// outbox row together (D5, D11).
/// </para>
/// </remarks>
/// <param name="db">The tracked, tenant-filtered write model.</param>
/// <param name="retention">The approved retention window (<c>Assets:RetentionDays</c>, WT56).</param>
/// <param name="clock">The command's clock reading; the domain never reads the clock itself.</param>
public sealed class ApproveVideoCommandHandler(
    IAppDbContext db,
    AssetRetentionOptions retention,
    IClock clock)
    : ICommandHandler<ApproveVideoCommand, ErrorOr<Success>>
{
    /// <summary>
    /// The most assets one approve re-stamps. A render records a video and its subtitle track, so this bounds a
    /// hand-written row set; the bound keeps a pathological episode from turning one accept into thousands of
    /// updates.
    /// </summary>
    private const int MaxRetentionAssets = 50;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(ApproveVideoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // The state precedes If-Match (the ReopenStory precedent): a cleared or stale tag must never let an
        // episode that is no longer under review be approved, and a retry sees the 409 it can act on.
        if (episode.Status != EpisodeStatus.VideoReview)
        {
            return Error.Conflict(ErrorCodes.EpisodeInvalidState, "Only a video under review can be approved.");
        }

        if (command.IfMatch is not null && !ETags.Matches(command.IfMatch, ETags.Episode(episode.Version)))
        {
            return Error.Conflict(ErrorCodes.EpisodeConflict, "The episode changed since it was read.");
        }

        var now = clock.UtcNow;
        var approved = episode.ApproveVideo(now);
        if (approved.IsError)
        {
            return approved.Errors;
        }

        // WT56: the ingest's unapproved window is overwritten on every live asset of the episode. The rows are
        // loaded tracked, so the write travels in this command's SaveChanges — never ExecuteUpdate, because the
        // entity method owns the rule. An asset already marked deleted is left alone.
        var retentionUntil = now + retention.Retention;
        var assets = await db.Assets
            .Where(asset => asset.EpisodeId == command.EpisodeId && asset.DeletedAt == null)
            .OrderBy(asset => asset.Id)
            .Take(MaxRetentionAssets)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var asset in assets)
        {
            asset.ScheduleRetention(retentionUntil);
        }

        return Result.Success;
    }
}
