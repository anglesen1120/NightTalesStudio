using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// The series' approved-episode structure history (02.8.1 plan §3.5, 02.8.4-T1): one no-tracking projection over
/// <c>episodes JOIN story_versions</c> on the approved version, newest version first.
/// </summary>
/// <remarks>
/// <para>
/// The projection is the variety guard's window and the empty-bible fallback of the idea data object, so it reads
/// only what those need: the episode id, the story version's title, the approval instant, and the content the D3
/// tags and beat signature are derived from. Drafts and reopened episodes have no
/// <c>approved_story_version_id</c> and are therefore never part of the history.
/// </para>
/// <para>
/// The version's <c>created_at</c> orders the rows (plan §3.5): a version can only be approved while it is
/// current, so the order matches approval order in practice. The row's date is the episode's own
/// <c>approved_at</c> when it has one, otherwise that creation instant.
/// </para>
/// <para>
/// Parsing reuses <c>StoryContentJson</c> and <see cref="StoryStructureTags.FromStoryContent"/>, so the tags and
/// the beats are derived exactly as the bible's summaries are. A malformed or structure-less historical row is
/// skipped, never thrown on: a series must be proposable even when one old version is damaged. The tenant filter
/// of the read model scopes every row to the caller's series.
/// </para>
/// </remarks>
/// <param name="readDb">The tenant-filtered read model of the caller's scope.</param>
public sealed class EpisodeStructureHistory(IReadDb readDb) : IEpisodeStructureHistory
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EpisodeStructureRef>> GetRecentAsync(Guid seriesId, int max, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        var rows = await readDb.Query<Episode>()
            .Where(episode => episode.SeriesId == seriesId && episode.ApprovedStoryVersionId != null)
            .Join(
                readDb.Query<StoryVersion>(),
                episode => episode.ApprovedStoryVersionId,
                version => (Guid?)version.Id,
                (episode, version) => new
                {
                    EpisodeId = episode.Id,
                    episode.ApprovedAt,
                    version.Title,
                    version.CreatedAt,
                    version.Content,
                })
            .OrderByDescending(row => row.CreatedAt)
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var history = new List<EpisodeStructureRef>(rows.Count);
        foreach (var row in rows)
        {
            if (!StoryContentJson.TryReadStored(row.Content, out var content))
            {
                continue;
            }

            var structure = StoryStructureTags.FromStoryContent(content);
            if (!HasStructure(structure))
            {
                continue;
            }

            history.Add(new EpisodeStructureRef(
                row.EpisodeId,
                row.Title,
                row.ApprovedAt ?? row.CreatedAt,
                structure,
                IdeaStructure.Signature(structure.Beats)));
        }

        return history;
    }

    /// <summary>Whether the derived tags carry any structure at all (a row written before D3 carries none).</summary>
    private static bool HasStructure(StoryStructureTags structure) =>
        structure.Beats.Count > 0
        || !string.IsNullOrEmpty(structure.SubGenre)
        || !string.IsNullOrEmpty(structure.Setting)
        || !string.IsNullOrEmpty(structure.Opening)
        || !string.IsNullOrEmpty(structure.TwistType);
}
