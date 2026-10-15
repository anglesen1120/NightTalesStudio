using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The read seam behind the music selector's avoid-recent rule (05 README D8, 05.3.2-T3): the track ids the series
/// picked most recently, newest first, read from <c>render_jobs.params</c>.
/// </summary>
/// <remarks>
/// <para>
/// One statement and no <c>DISTINCT</c>. It is raw SQL because <c>RenderJob.Params</c> is a value-converted jsonb
/// property: EF cannot translate a member path through it (<c>job.Params.Music.TrackId</c>), while the contract is the
/// JSON path <c>params-&gt;'music'-&gt;&gt;'track_id'</c> itself.
/// </para>
/// <para>
/// The tenant predicate is explicit. A raw statement does not pass through the D10 query filter, so the statement
/// restricts <c>r.user_id</c> to the scope's <see cref="ICurrentUser"/> (the interface's own SEC-5 requirement) and
/// joins the episode on id <em>and</em> owner; an anonymous scope reads nothing instead of everything.
/// </para>
/// <para>
/// <c>series_id</c> is not a column of <c>render_jobs</c> (00 §6), so the join through <c>episodes</c> is required
/// (WT10); the order is <c>render_jobs.id DESC</c> because the ids are UUIDv7 (D8) — which is creation order — and the
/// table has no <c>created_at</c> column (WT49).
/// </para>
/// </remarks>
/// <param name="database">The scope's read model; the statement runs on its connection.</param>
/// <param name="currentUser">The scope's actor: the tenant the statement is restricted to.</param>
public sealed class RecentMusicQuery(ReadDbContext database, ICurrentUser currentUser) : IRecentMusicQuery
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetRecentTrackIdsAsync(Guid seriesId, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        if (currentUser.Id is not { } userId)
        {
            // Anonymous: it owns no row, exactly what the D10 filter answers for such a scope.
            return [];
        }

        return await database.Database
            .SqlQuery<string>($"""
                SELECT r.params->'music'->>'track_id' AS "Value"
                FROM render_jobs r
                JOIN episodes e ON e.id = r.episode_id AND e.user_id = r.user_id
                WHERE r.user_id = {userId}
                  AND e.series_id = {seriesId}
                  AND r.params->'music' IS NOT NULL
                ORDER BY r.id DESC
                LIMIT {take}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
