using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Etags;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;

namespace NightTales.Application.Features.VideoReview;

/// <summary>
/// Reads one episode's current ETag (09 README D15) through the D8 write model, untracked, so asking for a tag
/// can never mutate the row it is about.
/// </summary>
/// <remarks>
/// It is scoped with the <see cref="IAppDbContext"/> it reads, and the read carries the D10 tenant filter: the
/// Worker's outbox scope pins the message's user before the handler is resolved, so a re-render reads the tag
/// of the owner's own episode and a foreign id answers <see langword="null"/> exactly like an unknown one.
/// </remarks>
/// <param name="db">The write model, read untracked.</param>
public sealed class EpisodeEtagReader(IAppDbContext db) : IEpisodeEtagReader
{
    /// <inheritdoc />
    public async ValueTask<string?> ForEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        var version = await db.Episodes
            .AsNoTracking()
            .Where(episode => episode.Id == episodeId)
            .Select(episode => (uint?)episode.Version)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return version is { } xmin ? ETags.Episode(xmin) : null;
    }
}
