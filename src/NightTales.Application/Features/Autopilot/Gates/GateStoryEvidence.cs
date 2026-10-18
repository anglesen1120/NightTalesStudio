namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The facts <c>gate_results</c> carries beside the per-check verdict of a story evaluation (02.8.2 plan §3.4:
/// the <c>similarity</c>, <c>real_world</c> and <c>moderation</c> blocks, plus the episode status at the moment
/// of the evaluation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids, codes and numbers only.</b> A real-world hit is recorded as its signal kinds and never as the matched
/// phrase or the excerpt (02.8.2-T2 owns those and keeps them in <c>audit_report</c>, D19/D22), and the
/// moderation block carries the D12 category names, never a flagged excerpt.
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
/// <param name="RealWorldKinds">The kinds of the signals that fired, as their wire names.</param>
/// <param name="ModerationFlagged"><c>audit_report.moderation.flagged</c>, or the gate's own moderator verdict (plan §3.1 checks 6–7).</param>
/// <param name="ModerationCategories">The blocking category names of the verdict, and nothing else.</param>
/// <param name="ModerationUnavailableDetail"><see cref="NightTales.Domain.Stories.Gates.GateDetailCodes.ModeratorUnavailable"/> when the moderator could not be reached; otherwise <see langword="null"/>.</param>
/// <param name="ModerationCheckedAt">When the moderation verdict was produced, or <see langword="null"/> when the block is absent.</param>
/// <param name="EpisodeStatusAtEvaluation">The episode's status wire name when the gate ran (plan §3.4).</param>
public sealed record GateStoryEvidence(
    decimal? SourceSimilarity,
    decimal? PastEpisodeSimilarity,
    int CorpusMax,
    bool RealWorldHit,
    IReadOnlyList<string> RealWorldKinds,
    bool ModerationFlagged,
    IReadOnlyList<string> ModerationCategories,
    string? ModerationUnavailableDetail,
    DateTimeOffset? ModerationCheckedAt,
    string EpisodeStatusAtEvaluation);
