using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// The corpus one novelty check compares against (02.8.1-T2 task Design): the series' newest approved episodes
/// and its live pending ideas, both bounded by <c>Autopilot:Ideas:Novelty:CorpusEpisodes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Episodes.</b> The read is one statement with the limit, the order and the projection in SQL
/// (<c>episodes JOIN story_versions</c> on <c>approved_story_version_id</c>, owner-qualified):
/// the ordered join is narrowed to the newest <c>LIMIT</c> versions by a subquery that selects their ids and
/// titles only, and the <c>content-&gt;&gt;'description'</c> cut to
/// <see cref="NoveltyDataObjectJson.SummaryMaxChars"/> is projected for those rows alone, joining the versions
/// back by id with the same <c>created_at DESC, id DESC</c> order repeated outside. So only <c>id</c>,
/// <c>title</c> and the 200-character summary travel and, more to the point, only the selected versions'
/// <c>story.v1</c> documents are read at all: no usable index can serve the <c>created_at</c> order, and a
/// projection applied before the sort would detoast every approved episode of the series. Drafts or reopened
/// episodes (no <c>approved_story_version_id</c>) are unreachable by the join. The order is
/// <c>created_at DESC, id DESC</c>: the version's creation is the only ordering a row is guaranteed to have,
/// and the UUIDv7 id breaks a tie deterministically, so the 1-based <c>past_episodes</c> indices the judge sees
/// are stable for one call.
/// </para>
/// <para>
/// The statement is raw because the projection is one JSON member of a <c>jsonb</c> column: EF cannot translate
/// a partial document read, so it would either materialise every version's full content or lose the
/// <c>LIMIT</c>. Nothing is concatenated into the SQL — the owner, the series, the cap and the limit are
/// parameters, and the tenant filter is restated by hand (<c>e.user_id = @userId</c>, the SEC-5 requirement of
/// every raw read), so an anonymous scope reads nothing instead of everything.
/// </para>
/// <para>
/// <b>Ideas.</b> The second read is a normal tenant-filtered EF query: the series' own <c>episode_ideas</c> rows
/// with <c>status IN ('proposed','picked')</c> — the ideas of an earlier run this slot has not resolved, which
/// is exactly what a new batch must not repeat — minus the batch's own ids (they are <c>proposed</c> too).
/// <c>rejected</c> and <c>used</c> rows are history the episode corpus already covers, so they are excluded.
/// The same <c>created_at DESC, id DESC</c> order and the same limit keep the comparison set bounded.
/// </para>
/// </remarks>
/// <param name="database">The scope's read model; both reads run on its connection and the second one carries its tenant filter.</param>
/// <param name="options">The <c>Autopilot:Ideas:Novelty</c> section: the corpus cap.</param>
public sealed class IdeaCorpusProvider(ReadDbContext database, IdeaNoveltyOptions options) : IIdeaCorpusProvider
{
    /// <summary>The hard ceiling of the corpus cap, whatever the configuration says (the validator allows 1..200).</summary>
    public const int MaxCorpusEpisodes = 200;

    /// <inheritdoc />
    public async ValueTask<NoveltyCorpus> GetAsync(Guid seriesId, IReadOnlyCollection<Guid> excludeIdeaIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(excludeIdeaIds);

        if (database.CurrentUserId is not { } userId)
        {
            // Anonymous: it owns no row, exactly what the D10 filter answers for such a scope.
            return new NoveltyCorpus([], []);
        }

        var limit = Math.Clamp(options.CorpusEpisodes, 1, MaxCorpusEpisodes);

        var episodes = await database.Database
            .SqlQuery<CorpusEpisode>($"""
                SELECT t.episode_id AS episode_id,
                       t.title AS title,
                       coalesce(left(sv.content->>'description', {NoveltyDataObjectJson.SummaryMaxChars}), '') AS summary
                FROM (
                    SELECT e.id AS episode_id,
                           sv.id AS version_id,
                           sv.title AS title,
                           sv.created_at AS created_at
                    FROM episodes e
                    JOIN story_versions sv ON sv.id = e.approved_story_version_id AND sv.user_id = e.user_id
                    WHERE e.user_id = {userId}
                      AND e.series_id = {seriesId}
                    ORDER BY sv.created_at DESC, sv.id DESC
                    LIMIT {limit}
                ) t
                JOIN story_versions sv ON sv.id = t.version_id
                ORDER BY t.created_at DESC, t.version_id DESC
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var excluded = excludeIdeaIds as Guid[] ?? [.. excludeIdeaIds];
        var pending = await database.Query<EpisodeIdea>()
            .Where(idea => idea.SeriesId == seriesId
                && (idea.Status == IdeaStatus.Proposed || idea.Status == IdeaStatus.Picked)
                && !excluded.Contains(idea.Id))
            .OrderByDescending(idea => idea.CreatedAt)
            .ThenByDescending(idea => idea.Id)
            .Take(limit)
            .Select(idea => new { idea.Id, idea.Title, idea.Logline })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new NoveltyCorpus(
            episodes,
            [.. pending.Select(row => new CorpusIdea(row.Id, row.Title, row.Logline))]);
    }
}
