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
    /// 429 <c>source_fetch_limit_reached</c> when the user already created
    /// <see cref="SourcesOptions.MaxUrlSourcesPerUserPerDay"/> url sources in the rolling window. The error carries
    /// <c>retryAfterSeconds</c>: the seconds until the oldest counted row leaves the window, at least 1.
    /// </summary>
    internal static async ValueTask<ErrorOr<Success>> CheckUrlCapAsync(
        IAppDbContext db,
        Guid userId,
        SourcesOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var windowStart = now - SourcesOptions.UrlWindow;

        var aggregate = await db.StorySources
            .AsNoTracking()
            .Where(source => source.UserId == userId
                && source.Kind == StorySourceKind.Url
                && source.CreatedAt > windowStart)
            .GroupBy(_ => 1)
            .Select(group => new { Counted = group.Count(), Oldest = group.Min(source => source.CreatedAt) })
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
}
