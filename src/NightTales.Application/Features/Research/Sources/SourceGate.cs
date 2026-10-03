using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources;

/// <summary>
/// The checks the three source commands share (02.5.1 T1 Design): the tenant-filtered episode load under the row
/// lock, the state gate, and the rolling 24 h url-source cap.
/// </summary>
internal static class SourceGate
{
    /// <summary>
    /// Takes the episode row lock and loads the episode tracked. Another user's id, an unknown id and a cancelled
    /// episode all answer <see cref="Errors.NotFound"/>, never 403.
    /// </summary>
    internal static async ValueTask<ErrorOr<Episode>> LoadEpisodeLockedAsync(
        IAppDbContext db,
        Guid episodeId,
        CancellationToken cancellationToken)
    {
        // The lock is the first statement of the command's transaction, so two parallel adds at the 9th/10th slot
        // serialise on the episode and the count below cannot be stale.
        await db.LockEpisodeForUpdateAsync(episodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == episodeId, cancellationToken)
            .ConfigureAwait(false);

        return episode is null || episode.Status == EpisodeStatus.Cancelled ? Errors.NotFound() : episode;
    }

    /// <summary>Sources are editable only while the story is a draft or in review (F26 precedent).</summary>
    internal static ErrorOr<Success> EnsureEditable(Episode episode) =>
        episode.Status is EpisodeStatus.Draft or EpisodeStatus.StoryReview
            ? Result.Success
            : Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "Sources can only be changed while the episode is a draft or in story review.");

    /// <summary>
    /// 429 <c>source_fetch_limit_reached</c> when the user already <em>created</em>
    /// <see cref="SourcesOptions.MaxUrlSourcesPerUserPerDay"/> url sources in the rolling window (02 F15/D8, CR-31): it
    /// counts the append-only <c>source_creations</c> ledger, never the live <c>story_sources</c> rows, so a delete
    /// frees no quota. The error carries <c>retryAfterSeconds</c>: the seconds until the oldest creation in the window
    /// leaves it, at least 1.
    /// </summary>
    /// <remarks>
    /// The episode row lock does not serialise two <em>different</em> episodes, so under READ COMMITTED two parallel
    /// adds (or an add and a retry) of one user could both read a stale count and together overshoot the cap
    /// (CR-1/SEC-1). Before counting, the command takes a transaction-scoped advisory lock keyed by the user — the
    /// <c>SecuritySql.LockUserAsync</c>/<c>EfRefreshTokenService.LockFamilyAsync</c> shape, with the dedicated seed
    /// <c>3</c> so it cannot collide with a refresh-family (<c>0</c>), user (<c>1</c>) or action-token-scope (<c>2</c>)
    /// lock. Every caller reaches this method after <see cref="LoadEpisodeLockedAsync"/>, so the lock order is always
    /// episode → user and no two paths can deadlock. The lock releases at commit/rollback with the command's unit of
    /// work; a call outside a transaction fails loudly instead of locking nothing.
    /// </remarks>
    internal static async ValueTask<ErrorOr<Success>> CheckUrlCapAsync(
        IAppDbContext db,
        Guid userId,
        SourcesOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // After the episode lock (LoadEpisodeLockedAsync), before the count: see the remarks.
        await db.LockUserSourceCapAsync(userId, cancellationToken).ConfigureAwait(false);

        var windowStart = now - SourcesOptions.UrlWindow;

        var aggregate = await db.SourceCreations
            .AsNoTracking()
            .Where(creation => creation.UserId == userId && creation.CreatedAt > windowStart)
            .GroupBy(_ => 1)
            .Select(group => new { Counted = group.Count(), Oldest = group.Min(creation => creation.CreatedAt) })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (aggregate is null || aggregate.Counted < options.MaxUrlSourcesPerUserPerDay)
        {
            return Result.Success;
        }

        var remaining = aggregate.Oldest + SourcesOptions.UrlWindow - now;
        var seconds = (int)Math.Max(1, (remaining.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);

        return Error.Failure(
            ErrorCodes.SourceFetchLimitReached,
            $"{options.MaxUrlSourcesPerUserPerDay} url sources per rolling 24 h are allowed.",
            new Dictionary<string, object>
            {
                [ErrorMetadata.RetryAfterSeconds] = seconds,
                [ErrorMetadata.Limit] = options.MaxUrlSourcesPerUserPerDay,
            });
    }

    /// <summary>
    /// Appends the creation record the cap counts, in the command's transaction (an add of a url source, or a retry
    /// that re-applies the cap). It holds no URL, note or episode (D13/D19).
    /// </summary>
    internal static void RecordCreation(IAppDbContext db, Guid userId, DateTimeOffset now) =>
        db.SourceCreations.Add(SourceCreation.Record(Guid.CreateVersion7(), userId, now));
}
