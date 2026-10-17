using System.Text.Json.Serialization;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The novelty measurement of one idea (02.8.1 plan §3.1 <c>novelty</c>): what the lexical shingle pass and
/// the one judge call of a batch concluded about it, and how big the corpus they saw was. It is the
/// <c>jsonb</c> payload of <c>episode_ideas.novelty</c>, written by 02.8.1-T2.
/// </summary>
/// <remarks>
/// The detail is measurement only: <see cref="EpisodeIdea.Score"/> and
/// <see cref="EpisodeIdea.Similarity"/> are the values the pick ranks on, and this document is what the
/// calibration histogram (<c>idea_novelty_*</c>) and the run log read back. Ids appear here
/// (<see cref="IdeaNoveltyLexical.MaxOverlapId"/>, <see cref="IdeaNoveltyJudge.MaxOverlapEpisodeId"/>)
/// because the row is server-side data, never model input. The <c>judge</c> key is always written —
/// <see langword="null"/> is how the degradation of the judge call is recorded (§4 U2) — so the stored
/// document and the calibration reader see one shape either way.
/// </remarks>
/// <param name="Lexical">The lexical pass' result; never <see langword="null"/> when the document exists.</param>
/// <param name="Judge">The judge call's result, or <see langword="null"/> when the judge was skipped or degraded.</param>
/// <param name="Corpus">The corpus both passes measured against.</param>
public sealed record IdeaNoveltyDetail(
    [property: JsonPropertyName("lexical")][property: JsonRequired] IdeaNoveltyLexical Lexical,
    [property: JsonPropertyName("judge")] IdeaNoveltyJudge? Judge,
    [property: JsonPropertyName("corpus")][property: JsonRequired] IdeaNoveltyCorpus Corpus);

/// <summary>
/// The lexical half of the novelty detail (02.8.1 plan §3.1): the highest Jaccard similarity the shingle
/// pass found and the corpus entry that produced it.
/// </summary>
/// <param name="Score">The highest similarity, 0…1 (0 when the corpus is empty).</param>
/// <param name="MaxOverlapKind">What the overlapping entry was: <c>episode</c> or <c>idea</c>.</param>
/// <param name="MaxOverlapId">The id of the overlapping entry, or <see langword="null"/> when nothing overlapped.</param>
public sealed record IdeaNoveltyLexical(
    [property: JsonPropertyName("score")][property: JsonRequired] decimal Score,
    [property: JsonPropertyName("max_overlap_kind")][property: JsonRequired] string? MaxOverlapKind,
    [property: JsonPropertyName("max_overlap_id")][property: JsonRequired] Guid? MaxOverlapId);

/// <summary>
/// The judge half of the novelty detail (02.8.1 plan §3.1, §4 U2): the model's novelty score for the idea
/// and the two components the scorer reuses, so no second call is paid.
/// </summary>
/// <param name="Score">How similar the judge found the idea to the corpus, 0…1.</param>
/// <param name="Fit">How well the idea fits the series topic, 0…1.</param>
/// <param name="HookStrength">How strong the judge found the hook, 0…1.</param>
/// <param name="Reason">The judge's one-line justification; server-side data, never logged (D19).</param>
/// <param name="MaxOverlapEpisodeId">The episode the judge named as the closest one, or <see langword="null"/>.</param>
public sealed record IdeaNoveltyJudge(
    [property: JsonPropertyName("score")][property: JsonRequired] decimal Score,
    [property: JsonPropertyName("fit")][property: JsonRequired] decimal Fit,
    [property: JsonPropertyName("hook_strength")][property: JsonRequired] decimal HookStrength,
    [property: JsonPropertyName("reason")][property: JsonRequired] string Reason,
    [property: JsonPropertyName("max_overlap_episode_id")][property: JsonRequired] Guid? MaxOverlapEpisodeId);

/// <summary>
/// The corpus both novelty passes measured against (02.8.1 plan §3.1): the approved episodes and the
/// pending ideas of the series, and the shingle width the language used.
/// </summary>
/// <param name="Episodes">How many approved episodes the corpus carried (0…100).</param>
/// <param name="PendingIdeas">How many pending ideas of the series it carried.</param>
/// <param name="ShingleN">The shingle width of the series language (vi 10, en 8; 02.5.6).</param>
public sealed record IdeaNoveltyCorpus(
    [property: JsonPropertyName("episodes")][property: JsonRequired] int Episodes,
    [property: JsonPropertyName("pending_ideas")][property: JsonRequired] int PendingIdeas,
    [property: JsonPropertyName("shingle_n")][property: JsonRequired] int ShingleN);
