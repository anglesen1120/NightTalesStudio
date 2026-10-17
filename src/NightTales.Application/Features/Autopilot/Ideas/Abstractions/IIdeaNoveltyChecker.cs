using ErrorOr;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// Measures one idea batch against the series' past (02.8.1-T2 plan §4 U1, task Design "Novelty"): the lexical
/// shingle pass for every candidate, then <b>one</b> judge call for the batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call per batch.</b> The judge sees all candidates together, so it cannot call two similar candidates
/// both fresh, and the batch pays for one call instead of one per candidate. A batch whose candidates are all
/// rejected on their lexical score makes no call at all: a candidate that is already rejected cannot be
/// un-rejected by a judge, so the call would only be billed for nothing (task Design "Novelty" item 3). The
/// result still carries a signal row for every candidate, with a null judge, and
/// <see cref="NoveltyBatchResult.JudgeUsed"/> stays <see langword="false"/>.
/// </para>
/// <para>
/// <b>One retry, then degrade (D7, plan §4 U2).</b> A <c>TextCallInvalidOutput</c> that is neither an output cut
/// at <c>max_output_tokens</c> nor a data object over <c>Story:Prompt:MaxDataObjectBytes</c> earns the run's
/// second and last call, under <c>attempt = 2</c>. Both of those refusals are final on the first answer — the
/// retry would be cut at the same cap or build the very same object — so they degrade at once. Every dispatch,
/// the retry included, resolves the novelty call's own worst case and clears <see cref="IBudgetGuard"/> with it
/// before it is sent (D1: before every paid dispatch), exactly as the ideas call does. Whatever an attempt
/// answers, and every other failure — a provider error, an outage, a refusal — <b>degrades the judge and never
/// the batch</b>: the lexical scores stand, the signals take <see cref="IdeaSignals.LexicalOnly"/>'s neutral
/// components, and the checker answers success. The two failures that are <b>not</b> a degrade-and-continue are
/// the ones that mean the run must stop: a <c>ProviderFundsExhausted</c> (the account cannot pay for anything
/// more) and the platform spend cap's refusal, which propagate as the run's error before anything is stored.
/// </para>
/// <para>
/// <b>Defensive mapping (Decision 10).</b> The answer is a list of rows keyed by the candidate's 0-based
/// <c>index</c>, so the caller must not let a model's arithmetic decide an idea's fate: a row whose index is
/// out of range, a duplicate index and a candidate no row mentions all leave that candidate's judge
/// <see langword="null"/> (judged as far as the lexical pass is concerned) rather than dropping or misfiling
/// it. A garbled <c>max_overlap_episode_id</c> likewise degrades to <see langword="null"/>: the id is resolved
/// from the 1-based index against the corpus that was actually sent, and an index that does not resolve names
/// no episode.
/// </para>
/// <para>
/// <b>Thresholds are applied to the raw scores</b> (lexical and judge), never to the weighted similarity the
/// scorer builds: the checker is the only place that knows both raw readings, and it reads
/// <c>Autopilot:Ideas:Novelty:LexicalMax</c> and <c>:JudgeMax</c> from <see cref="IdeaNoveltyOptions"/>. A
/// score strictly greater than its maximum rejects the candidate, so a score exactly at the threshold passes
/// (the boundary the tests pin).
/// </para>
/// </remarks>
public interface IIdeaNoveltyChecker
{
    /// <summary>Measures the batch: the lexical pass for every candidate, then the batch's judge call.</summary>
    /// <param name="batch">The candidates to measure, in the batch's order; the position is what the judge's 0-based index means.</param>
    /// <param name="corpus">The corpus loaded by the caller: its own rows are already excluded by id.</param>
    /// <param name="lang">The series' language; the shingle width (vi 10, en 8) comes from it, and its wire name is a control value of the judge's data object.</param>
    /// <param name="topic">
    /// The series' topic, the judge's other control value. It is a parameter here because the judge request
    /// (<see cref="NoveltyJudgeRequest.Topic"/>) carries it and nothing else on this call path does: the batch
    /// holds candidates, and the corpus holds episodes and ideas.
    /// </param>
    /// <param name="run">The run the paid call is billed to; its attempt is rewritten per call.</param>
    /// <param name="ct">Cancels the reads and the calls.</param>
    /// <returns>A signal row for every candidate of the batch, or the run-stopping error of the paid call.</returns>
    ValueTask<ErrorOr<NoveltyBatchResult>> CheckAsync(
        IReadOnlyList<(Guid IdeaId, IdeaCandidate Candidate)> batch,
        NoveltyCorpus corpus,
        Language lang,
        string topic,
        RunContext run,
        CancellationToken ct);
}

/// <summary>
/// Runs the series' content policy against one idea batch (02.8.1-T2 plan §4 U2, task Design "Content
/// policy"): the banned-theme backstop, then the moderator, then the true-crime research flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Banned themes first, and they are free.</b> A candidate whose text carries a banned theme of the series
/// is rejected on the spot and the moderator is never asked about it: the flag says the series must not tell
/// that story at all, so a second opinion is not paid for (task Design "Content policy" item 1).
/// </para>
/// <para>
/// <b>The moderator is asked once per candidate that survived</b>, about its logline and hook together, as the
/// brief epoch of one prompt (D12's <c>brief</c> stage). A flagged candidate is rejected and the refusal is
/// written to <c>moderation_events</c> — one row per flagged candidate, carrying the categories and at most 300
/// characters of the flagged text — before the batch is answered, so the owner's moderation log shows what was
/// stopped and why. The whole pre-check is skipped when the batch is empty or
/// <c>Autopilot:Ideas:Policy:ModerationEnabled</c> is <see langword="false"/>; the banned-theme check still
/// runs either way.
/// </para>
/// <para>
/// <b>Unavailability is a configuration decision.</b> A moderator that refuses or cannot answer is not a clean
/// batch, but it is not necessarily the run's end either: the shipped <c>Autopilot:Ideas:Policy:FailClosed</c>
/// is <see langword="false"/>, so the batch is cleared and the counter <c>moderation_unavailable_total</c>
/// records the outage; switching the flag on rejects every candidate of the batch with
/// <see cref="IdeaRejectionReason.PolicyModerated"/> instead. The counter is written either way, because the
/// flag alone cannot say what happened.
/// </para>
/// <para>
/// <b>Research, not rejection.</b> A true-crime series' candidate that reads as real-world framing — the
/// framing lexicon or a dated incident — comes back with <see cref="PolicyVerdict.RequiresResearch"/>, which
/// follows the idea to the story gate and the owner's fact-sheet approval. The idea level never rejects on it
/// (F31/F33).
/// </para>
/// <para>
/// <b>No error path.</b> A policy refusal is a candidate's outcome, not the run's: the pre-check answers a
/// verdict for every candidate and never an <c>ErrorOr</c>.
/// </para>
/// </remarks>
public interface IIdeaPolicyChecker
{
    /// <summary>Runs the banned-theme, moderation and framing checks of one batch.</summary>
    /// <param name="batch">The candidates to check; every one of them gets a verdict.</param>
    /// <param name="ctx">The series' policy inputs: its topic and its ban list (never logged).</param>
    /// <param name="run">The run the refusal evidence is linked to.</param>
    /// <param name="ct">Cancels the moderator calls and the evidence writes.</param>
    /// <returns>A verdict for every candidate of the batch, and whether the moderator answered.</returns>
    ValueTask<PolicyBatchResult> CheckAsync(
        IReadOnlyList<(Guid IdeaId, IdeaCandidate Candidate)> batch,
        IdeaPolicyContext ctx,
        RunContext run,
        CancellationToken ct);
}
