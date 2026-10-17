using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// One candidate's measured novelty (02.8.1-T2, task Design "Novelty"): the lexical pass's verdict and, when
/// the batch's judge call answered, the judge's.
/// </summary>
/// <remarks>
/// The thresholds are applied to the <b>raw</b> scores of these two passes, never to the weighted
/// <c>similarity</c> of an <see cref="IdeaSignals"/> document: the two readings differ and the stricter one is
/// the run's decision. <see cref="LexicalScore"/> is the shingle overlap with the corpus (the domain's
/// <c>IdeaNoveltyLexical</c>), and <see cref="Judge"/> is <see langword="null"/> when the batch made no judge
/// call (every candidate was rejected on its lexical score) or when the call degraded.
/// </remarks>
/// <param name="LexicalScore">The closest shingle overlap with the corpus, 0…1; 0 when nothing overlapped.</param>
/// <param name="LexicalOverlapId">The corpus row the overlap named — an episode id or a pending idea id — or <see langword="null"/>.</param>
/// <param name="Judge">The judge's verdict for this candidate, or <see langword="null"/> when the batch made no judge call or the answer carried no usable row for it.</param>
public sealed record NoveltySignals(decimal LexicalScore, Guid? LexicalOverlapId, NoveltyJudge? Judge);

/// <summary>
/// One candidate's novelty as the judge measured it (02.8.1-T2, task Design "Novelty"), mapped from the
/// model's answer row: the scores the planner reuses and the overlap the run log reports.
/// </summary>
/// <remarks>
/// <see cref="MaxOverlapEpisodeId"/> is the server's resolution of the answer's 1-based index against the
/// corpus that was sent; a garbled, out-of-range or ambiguous index is <see langword="null"/>, and so is one
/// the model omitted (the answer's row itself is what makes a candidate judged). The reason is trimmed, capped
/// at 300 characters and stripped of control characters before it is stored, and it is never logged (D19).
/// </remarks>
/// <param name="Score">How similar the judge found the idea to the past, clamped to 0…1 (1 = the same story already told).</param>
/// <param name="Fit">How well the idea fits the series' topic, clamped to 0…1; a value only the judge reports, for a candidate its answer covered. When an answered batch's answer carries no row for this candidate the planner substitutes the neutral 0.5 (<see cref="IdeaSignals.Unjudged"/>), and a whole-batch degradation has no judge row at all and keeps <see cref="IdeaSignals.LexicalOnly"/>'s fit of 1.0.</param>
/// <param name="HookStrength">The judge's reading of the hook, clamped to 0…1; the planner substitutes the neutral 0.5 when an answered batch's answer carried no row for this candidate (<see cref="IdeaSignals.Unjudged"/>) and, for the same value, when the whole batch degraded (<see cref="IdeaSignals.LexicalOnly"/>).</param>
/// <param name="MaxOverlapEpisodeId">The past episode the judge named, or <see langword="null"/>.</param>
/// <param name="Reason">The judge's justification, trimmed, capped and sanitised for storage.</param>
public sealed record NoveltyJudge(
    decimal Score,
    decimal Fit,
    decimal HookStrength,
    Guid? MaxOverlapEpisodeId,
    string Reason);

/// <summary>
/// One batch's novelty result (02.8.1-T2, task Design "Novelty"): a signal row for <b>every</b> candidate of
/// the batch and whether the judge call contributed to them.
/// </summary>
/// <remarks>
/// Every candidate is a key of <see cref="ByIdeaId"/>: the checker never drops a candidate, it degrades its
/// <see cref="NoveltySignals.Judge"/> to <see langword="null"/>. That is what lets the planner reject on the
/// lexical score alone and still store a complete <c>novelty</c> document for every row. A candidate the
/// answer did not score is judged too (a null judge is legal), and one score row may not cost another
/// candidate its verdict. <see cref="JudgeUsed"/> is the caller's <c>judge_used</c> flag: <see langword="false"/>
/// when the batch made no call (every candidate was blocked lexically) or when the call degraded, and
/// <see langword="true"/> once an answer was consumed — it is the answer's rows, not the attempt's, that the
/// flag reports.
/// </remarks>
/// <param name="ByIdeaId">The signals keyed by the batch's idea ids.</param>
/// <param name="JudgeUsed">Whether the batch's answer was consumed: <see langword="true"/> when the call
/// answered and its rows were applied, <see langword="false"/> when no call was made or the call degraded. It is
/// what tells a batch whose judge was lost for every candidate together (all of them take
/// <see cref="IdeaSignals.LexicalOnly"/>'s neutrals, ranking unaffected) from a batch that was answered and left
/// one candidate out of the answer (that candidate takes <see cref="IdeaSignals.Unjudged"/>'s neutral fit and
/// cannot outrank the judged ones on it).</param>
public sealed record NoveltyBatchResult(IReadOnlyDictionary<Guid, NoveltySignals> ByIdeaId, bool JudgeUsed);
