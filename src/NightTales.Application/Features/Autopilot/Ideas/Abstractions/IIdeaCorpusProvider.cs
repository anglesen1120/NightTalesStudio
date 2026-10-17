namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// One approved episode of the series as the novelty pass sees it (02.8.1-T2, task Design "Novelty"): its id
/// and the two texts the corpus compares and the judge reads.
/// </summary>
/// <remarks>
/// The summary is the <c>story_versions.content.description</c> of the approved version truncated to 200
/// characters, so the corpus is bounded whatever the owner wrote (the provider owns the truncation).
/// </remarks>
/// <param name="EpisodeId">The episode; the id maps back from the judge's 1-based index and is the lexical overlap's identity.</param>
/// <param name="Title">The approved version's title.</param>
/// <param name="Summary">The approved version's description, at most 200 characters.</param>
public sealed record CorpusEpisode(Guid EpisodeId, string Title, string Summary);

/// <summary>
/// One pending idea of the series as the novelty pass sees it (02.8.1-T2, task Design "Novelty"): a
/// <c>proposed</c> or <c>picked</c> row of an earlier run, which this batch must not repeat.
/// </summary>
/// <param name="IdeaId">The <c>episode_ideas</c> row; the lexical overlap's identity.</param>
/// <param name="Title">The idea's title as proposed.</param>
/// <param name="Logline">The idea's one-line premise as proposed.</param>
public sealed record CorpusIdea(Guid IdeaId, string Title, string Logline);

/// <summary>
/// The corpus one idea batch is measured against (02.8.1-T2, task Design "Novelty"): the newest approved
/// episodes of the series and its pending ideas, read once before the batch is scored.
/// </summary>
/// <remarks>
/// The batch's own new rows are excluded by id at the read (<c>excludeIdeaIds</c>), because they are stored
/// <c>proposed</c> too and would otherwise be their own closest neighbour.
/// </remarks>
/// <param name="Episodes">The newest approved episodes, newest first; empty for a series' first episode.</param>
/// <param name="PendingIdeas">The series' <c>proposed</c>/<c>picked</c> ideas of earlier runs, newest first; empty when it has none.</param>
public sealed record NoveltyCorpus(IReadOnlyList<CorpusEpisode> Episodes, IReadOnlyList<CorpusIdea> PendingIdeas);

/// <summary>
/// Reads the novelty corpus of one idea batch (02.8.1-T2, task Design "Novelty").
/// </summary>
/// <remarks>
/// <para>
/// One source keeps the scoring honest: the approved <c>story_versions</c> rows, never the bible's summaries,
/// which the owner edits (task file [DECISION]). Both reads are series-scoped, tenant-filtered, bounded in SQL
/// (<c>Autopilot:Ideas:Novelty:CorpusEpisodes</c>) and read-only; the implementation logs no content (D19).
/// </para>
/// <para>
/// The provider reads the rows; the lexical pass and the judge call belong to
/// <see cref="IIdeaNoveltyChecker"/>.
/// </para>
/// </remarks>
public interface IIdeaCorpusProvider
{
    /// <summary>Reads the corpus of one series.</summary>
    /// <param name="seriesId">The series, already resolved by the caller in its own tenant.</param>
    /// <param name="excludeIdeaIds">
    /// The batch's own new idea ids, which the pending-idea read must leave out: they are <c>proposed</c>
    /// rows of this very run.
    /// </param>
    /// <param name="ct">Cancels the reads.</param>
    /// <returns>The episodes and the pending ideas of the series; both empty when the series has neither.</returns>
    ValueTask<NoveltyCorpus> GetAsync(Guid seriesId, IReadOnlyCollection<Guid> excludeIdeaIds, CancellationToken ct);
}
