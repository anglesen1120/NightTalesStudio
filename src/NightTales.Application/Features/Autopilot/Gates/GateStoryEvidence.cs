namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The facts <c>gate_results</c> carries beside the per-check verdict of a story evaluation (02.8.2 plan §3.4:
/// the <c>similarity</c>, <c>real_world</c> and <c>moderation</c> blocks, plus the episode status at the moment
/// of the evaluation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids, codes and numbers only.</b> A real-world hit is recorded as its signal kinds and the D22 ids it fired on,
/// never as the matched phrase or the excerpt, and the moderation block carries the D12 category names and the ids
/// of the flagged segments, never a flagged excerpt (D19/D22, 02.8.2-T2 plan §3.4).
/// </para>
/// <para>
/// The evidence travels beside <c>StoryGateResult</c> rather than inside it: the Domain verdict must stay a pure
/// function of the check inputs, while these are the report's own context lines that no check decides on.
/// </para>
/// </remarks>
/// <param name="SourceSimilarity"><c>story_versions.similarity_score</c>, or <see langword="null"/> when it was never computed.</param>
/// <param name="PastEpisodeSimilarity">The similarity against the series' own past episodes, or <see langword="null"/> when the corpus had nothing to compare with.</param>
/// <param name="CorpusMax">How many past episodes the score was computed against (<c>corpus_max</c>).</param>
/// <param name="RealWorldHit">Whether the real-world signal fired (plan §3.1 check 8).</param>
/// <param name="RealWorldKinds">The kinds of the signals that fired, as their wire names, once each.</param>
/// <param name="RealWorldSegments">
/// The D22 ids the signals fired on (<c>title</c>, <c>hook</c>, <c>p:&lt;i&gt;</c>, <c>ending</c>, <c>meta</c>), once
/// each and in hit order; empty for a clean story, and an address outside that shape (a claim's own wording, say) is
/// never listed — the hit it belongs to still counts in <paramref name="RealWorldKinds"/>.
/// </param>
/// <param name="ModerationFlagged">The content gate's own verdict over the current content (plan §3.1 check 7).</param>
/// <param name="ModerationCategories">The blocking category names of the verdict, and nothing else.</param>
/// <param name="ModerationSegments">
/// The D22 ids the flag was attributed to, plus <c>scene:&lt;i&gt;</c> for a flagged scene prompt, once each; empty
/// on a pass or when nothing could be attributed.
/// </param>
/// <param name="ModerationUnavailableDetail"><see cref="NightTales.Domain.Stories.Gates.GateDetailCodes.ModeratorUnavailable"/> when the moderator could not be reached; otherwise <see langword="null"/>.</param>
/// <param name="ModerationCheckedAt">When the moderation verdict was produced.</param>
/// <param name="EpisodeStatusAtEvaluation">The episode's status wire name when the gate ran (plan §3.4).</param>
public sealed record GateStoryEvidence(
    decimal? SourceSimilarity,
    decimal? PastEpisodeSimilarity,
    int CorpusMax,
    bool RealWorldHit,
    IReadOnlyList<string> RealWorldKinds,
    IReadOnlyList<string> RealWorldSegments,
    bool ModerationFlagged,
    IReadOnlyList<string> ModerationCategories,
    IReadOnlyList<string> ModerationSegments,
    string? ModerationUnavailableDetail,
    DateTimeOffset? ModerationCheckedAt,
    string EpisodeStatusAtEvaluation);
