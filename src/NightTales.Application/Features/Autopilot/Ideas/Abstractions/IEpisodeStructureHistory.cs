using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// One approved episode of a series as the idea steps read it (02.8.1 plan §3.5): the structure block, the outline
/// signature and the title — never the story body.
/// </summary>
/// <remarks>
/// <para>
/// T1 uses <see cref="Title"/> (with <see cref="ApprovedAt"/>) as the fallback the prompt reads when the bible
/// carries no summaries. 02.8.4-T1's variety guard reads <see cref="Structure"/> and
/// <see cref="BeatSignature"/> for its rotation and template-repeat rules, which is why the record carries the
/// whole structure block and not just the title.
/// </para>
/// <para>
/// <see cref="EpisodeId"/> stays in the record even though T1 sends no id to the model: the guard's run log
/// (10.4.3) names the episode a rejection traces to, and the DTO is the peer contract 02.8.4-T1 consumes.
/// </para>
/// <para>
/// <see cref="ApprovedAt"/> is the approved story version's creation time, the plan's proxy for the approval
/// stamp: a version can only be approved while it is current, so the order it imposes is the order in practice.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode's id.</param>
/// <param name="Title">The approved story version's title.</param>
/// <param name="ApprovedAt">The approved story version's creation time.</param>
/// <param name="Structure">The D3 structure tags of that version.</param>
/// <param name="BeatSignature">The version's paragraph beats joined with <c>&gt;</c>.</param>
public sealed record EpisodeStructureRef(
    Guid EpisodeId,
    string Title,
    DateTimeOffset ApprovedAt,
    StoryStructureTags Structure,
    string BeatSignature);

/// <summary>
/// Reads the approved-episode structure history of one series, newest first (02.8.1 plan §3.5).
/// </summary>
/// <remarks>
/// One query fixed once: <c>episodes</c> JOIN <c>story_versions</c> on <c>approved_story_version_id</c>, ordered
/// by the version's creation time descending, capped by the caller. Drafts and reopened episodes are excluded by
/// construction — the join is on the approved version only. The port exists so the planner and 02.8.4-T1's
/// guard share one reader instead of two drifting projections.
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
