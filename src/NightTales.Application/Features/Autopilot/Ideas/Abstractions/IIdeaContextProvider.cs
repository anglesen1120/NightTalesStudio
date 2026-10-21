using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// The series-level material of one idea batch (02.8.1-T1 plan §4 U1 step 1): everything the prompt needs that is
/// not already the run's, read once so no later step re-reads the series.
/// </summary>
/// <remarks>
/// <para>
/// The two <c>autopilot_settings</c> keys (<c>tone</c>, <c>banned_themes</c>) are owned by 02.8.3-T1; until that
/// schema lands the implementation reads them defensively and answers <see langword="null"/> and empty, never an
/// error (TL ruling #3, 2026-09-24).
/// </para>
/// <para>
/// <see cref="History"/> is the series' approved-episode structure history (the newest
/// <c>Autopilot:Ideas:HistoryLimit</c> episodes). T1 uses it only to stand in for an absent bible's summaries;
/// 02.8.4-T1's variety guard reads the same list for its rotation and template-repeat rules.
/// </para>
/// <para>
/// <see cref="ResearchNotes"/> is at most five of the series' newest <c>story_sources</c> notes: the
/// <c>user_note</c> text truncated to 400 characters, and nothing from a <c>url</c> source — neither its URL
/// nor its title (the acceptance check <c>…_NoIdsNoUrls</c>; the extracted source text is never read, D19). It
/// is what travels inside the one D6 data object, never into the system prompt.
/// </para>
/// </remarks>
/// <param name="Topic">The series' narrative topic, snake_case (<c>ghost</c>, <c>spiritual</c>, <c>true_crime</c>).</param>
/// <param name="Language">The series' content language, snake_case (<c>vi</c>, <c>en</c>).</param>
/// <param name="Tone">The series' tone, or <see langword="null"/> when none is configured.</param>
/// <param name="BannedThemes">The series' banned themes; empty when none are configured. At most 20 × 60 characters by contract.</param>
/// <param name="Bible">The bible snapshot, or <see langword="null"/> when the series has no bible content.</param>
/// <param name="History">The recent approved episodes, newest first; empty for a new series.</param>
/// <param name="ResearchNotes">Up to five notes of the series, newest first; empty when it has none.</param>
/// <param name="Count">The configured number of ideas to ask for (<c>Autopilot:Ideas:Count</c>); the planner clamps it.</param>
public sealed record IdeaContext(
    string Topic,
    string Language,
    string? Tone,
    IReadOnlyList<string> BannedThemes,
    BibleSnapshot? Bible,
    IReadOnlyList<EpisodeStructureRef> History,
    IReadOnlyList<ResearchNoteRef> ResearchNotes,
    int Count);

/// <summary>
/// Reads the series material of one idea batch (02.8.1-T1 plan §4 U1 step 1).
/// </summary>
/// <remarks>
/// The planner calls this once per run, after it has resolved the series under the caller's tenant, so the
/// implementation may assume the series exists: the bible, the settings, the history and the notes all descend
/// from it. It reads; it never writes, and it logs no content (D19).
/// </remarks>
public interface IIdeaContextProvider
{
    /// <summary>Reads the context of one series.</summary>
    /// <param name="seriesId">The series, already resolved by the caller in its own tenant.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The series material of one batch.</returns>
    /// <exception cref="InvalidOperationException">
    /// The series row vanished between the caller's own tenant check and this read: data drift, never user input,
    /// so it is thrown rather than answered (the <c>OpenAiBibleProposalGenerator</c> boundary shape).
    /// </exception>
    ValueTask<IdeaContext> GetAsync(Guid seriesId, CancellationToken ct);
}
