using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// Reads the approved-episode structure history of one series, newest first (02.8.1 plan §3.5).
/// </summary>
/// <remarks>
/// <para>
/// One query fixed once: <c>episodes</c> JOIN <c>story_versions</c> on <c>approved_story_version_id</c>, ordered
/// by the version's creation time descending, capped by the caller. Drafts and reopened episodes are excluded by
/// construction — the join is on the approved version only. The port exists so the planner and 02.8.4-T1's
/// guard share one reader instead of two drifting projections.
/// </para>
/// <para>
/// The row type is <see cref="EpisodeStructureRef"/>, which lives in the Domain beside
/// <see cref="StoryStructureTags"/> (02.8.4-T1): the variety guard is pure and the Domain cannot reference this
/// port, so the record is domain-owned and the port only returns it.
/// </para>
/// </remarks>
public interface IEpisodeStructureHistory
{
    /// <summary>Reads the newest approved episodes of one series.</summary>
    /// <param name="seriesId">The series, already resolved by the caller in its own tenant.</param>
    /// <param name="max">The most rows to return; must be positive.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The episodes, newest first; fewer than <paramref name="max"/> for a young series, empty for a new one.</returns>
    ValueTask<IReadOnlyList<EpisodeStructureRef>> GetRecentAsync(Guid seriesId, int max, CancellationToken ct);
}
