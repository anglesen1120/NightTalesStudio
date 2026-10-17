using System.Globalization;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The novelty pass of one idea batch (02.8.1-T2 plan §4 U2, task Design "Novelty"): the lexical shingle pass for
/// every candidate against the series' corpus, then one judge call for the batch while it can still change the
/// outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call per batch, and none when none is needed.</b> The judge sees the whole batch, so it cannot call two
/// similar candidates both fresh, and the batch pays once. A batch whose every candidate already fails
/// <c>Autopilot:Ideas:Novelty:LexicalMax</c> makes no call: a rejected candidate cannot be un-rejected by a
/// judge, so that call would be billed for nothing. The result carries a signal row for every candidate either
/// way.
/// </para>
/// <para>
/// <b>The judge never fails the run.</b> Whatever the one retry answers, and whatever a provider error, an
/// outage or a refusal answers — a data object the judge refuses as over its byte ceiling included — the batch
/// degrades to lexical-only signals: the lexical scores stand and the judge's absent components take
/// <see cref="IdeaSignals.LexicalOnly"/>'s neutral values. Only a <c>ProviderFundsExhausted</c> and the platform
/// spend cap's refusal stop the run, because both mean the account cannot pay for anything more.
/// </para>
/// <para>
/// <b>Every dispatch is checked before it is bought.</b> The caller's admission reserved the ideas call, so each
/// judge dispatch — attempt 1 and the one retry — resolves the judge's own worst case and asks
/// <see cref="IBudgetGuard"/> with it (D1: before every paid dispatch), which is what keeps the platform cap the
/// last word on a run admitted just underneath it.
/// </para>
/// <para>
/// <b>Nothing the model wrote reaches a log line or a tag.</b> Scores are numbers, the counts are counts, and the
/// log lines carry the run's id and closed detail codes only (D19); the judge's reason is server-side data of the
/// stored <c>novelty</c> document, cleaned and capped there.
/// </para>
/// </remarks>
/// <param name="options">The thresholds (<c>Autopilot:Ideas:Novelty</c>): they decide both the rejection and whether the judge call is worth making.</param>
/// <param name="judge">The one paid call of the batch; it meters its own usage row (D14).</param>
/// <param name="budgetGuard">The platform spend cap, re-checked before every judge dispatch (D1).</param>
/// <param name="costEstimator">The worst case of one judge call: the bound each dispatch's cap check is made with.</param>
/// <param name="moderationEvents">The one writer of <c>moderation_events</c> (D12): a refused judge call writes one row.</param>
/// <param name="metrics">The calibration histograms of the two scores.</param>
/// <param name="logger">The outcome log of the pass.</param>
public sealed partial class IdeaNoveltyChecker(
    IdeaNoveltyOptions options,
    IIdeaNoveltyJudge judge,
    IBudgetGuard budgetGuard,
    IIdeaCostEstimator costEstimator,
    IModerationEventWriter moderationEvents,
    IdeaMetrics metrics,
    ILogger<IdeaNoveltyChecker> logger) : IIdeaNoveltyChecker
{
    /// <summary>The cap the judge's reason is stored with (task file: "cap <c>reason</c> at 300 chars on write").</summary>
    private const int ReasonMaxChars = 300;

    /// <summary>What the judge call did for the batch.</summary>
    private enum JudgeOutcome
    {
        /// <summary>The answer was consumed: the per-candidate judge signals come from it.</summary>
        Used,

        /// <summary>The call was refused or failed: the batch keeps its lexical signals only.</summary>
        Degraded,
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<NoveltyBatchResult>> CheckAsync(
        IReadOnlyList<(Guid IdeaId, IdeaCandidate Candidate)> batch,
        NoveltyCorpus corpus,
        Language lang,
        string topic,
        RunContext run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(run);

        var lexical = LexicalCorpus.Build(BuildCorpusEntries(corpus), lang);
        var lexicalMax = options.EffectiveLexicalMax;
        var signals = new NoveltySignals[batch.Count];
        var anyEligible = false;

        for (var index = 0; index < batch.Count; index++)
        {
            var match = lexical.ScoreCandidate(IdeaNoveltyLexicalScorer.CandidateText(batch[index].Candidate));
            signals[index] = new NoveltySignals(match.Score, match.OverlapId, Judge: null);
            metrics.NoveltyLexical(match.Score);

            // The comparison the planner rejects with, applied here as well: the raw lexical score against the
            // raw ceiling, strictly greater. It decides whether this batch still has an outcome a judge could
            // change; the planner owns the rejection itself.
            anyEligible |= match.Score <= lexicalMax;
        }

        var judgeUsed = false;
        if (anyEligible)
        {
            var asked = await AskJudgeAsync(batch, corpus, lang, topic, run, ct).ConfigureAwait(false);
            if (asked.IsError)
            {
                return asked.Errors;
            }

            judgeUsed = asked.Value.Outcome == JudgeOutcome.Used;
            if (judgeUsed)
            {
                ApplyJudgeRows(asked.Value.Rows, corpus, signals);
            }
        }

        LogPass(
            run.GenerationRunId,
            batch.Count,
            corpus.Episodes.Count,
            corpus.PendingIdeas.Count,
            judgeUsed);

        return Build(signals, batch, judgeUsed);
    }

    /// <summary>
    /// Makes the batch's judge call, with the single D7 retry an invalid output earns: two attempts at most, the
    /// second under <c>attempt = 2</c>, which is what the D7 correlation id
    /// (<c>{generationRunId}:novelty:{attempt}</c>) and the run's attempt counter read. Each attempt is a paid
    /// dispatch of its own, so each one resolves its bound and clears the platform cap before it is sent (D1).
    /// </summary>
    /// <param name="batch">The candidates, in the batch's order.</param>
    /// <param name="corpus">The corpus that was measured; its episodes are what the judge may name by index.</param>
    /// <param name="lang">The series' language, written into the data object's control value.</param>
    /// <param name="topic">The series' topic, a control value of the prompt's data object.</param>
    /// <param name="run">The run the call is billed to.</param>
    /// <param name="ct">Cancels the calls.</param>
    /// <returns>The answer's rows with <see cref="JudgeOutcome.Used"/>, an empty degradation, or the error that stops the run.</returns>
    private async ValueTask<ErrorOr<(JudgeOutcome Outcome, IReadOnlyList<NoveltyScoreV1> Rows)>> AskJudgeAsync(
        IReadOnlyList<(Guid IdeaId, IdeaCandidate Candidate)> batch,
        NoveltyCorpus corpus,
        Language lang,
        string topic,
        RunContext run,
        CancellationToken ct)
    {
        var runId = run.GenerationRunId;
        var candidates = batch.Select(entry => entry.Candidate).ToArray();

        for (var attempt = 1; ; attempt++)
        {
            // D1: every paid dispatch is checked, this batch's first one included — the caller's admission
            // reserved the ideas call, never the judge's. The bound is resolved first (a run with no usable price
            // version or no shipped prompt fails closed rather than dispatching uncosted), then the platform cap
            // is asked with this dispatch's own worst case and the run's own id, which the guard leaves out of
            // the open sum. A refusal is the run's spend-cap error: nothing is stored.
            var bound = await costEstimator
                .NoveltyWorstCaseAsync(run.PriceVersion, ct)
                .ConfigureAwait(false);
            if (bound.IsError)
            {
                LogFailed(runId, bound.FirstError.Code);

                return bound.Errors;
            }

            var allowed = await budgetGuard
                .CheckAsync(new CostBound(bound.Value.WorstCaseUsd), ct, runId)
                .ConfigureAwait(false);
            if (allowed.IsError)
            {
                LogFailed(runId, IdeaCallFaults.SpendCapReached);

                return IdeaErrors.SpendCapReached;
            }

            var request = new NoveltyJudgeRequest(
                run with { Attempt = attempt },
                WireLanguage(lang),
                topic,
                candidates,
                corpus.Episodes);

            var result = await judge.JudgeAsync(request, ct).ConfigureAwait(false);

            switch (result)
            {
                case TextCallOk<NoveltyV1> ok:
                    return (JudgeOutcome.Used, ok.Draft.Scores);

                case TextCallInvalidOutput<NoveltyV1> invalid:
                    LogInvalidOutput(runId, attempt, invalid.RuleIds.Count);

                    // The precedent's rule (IdeaPlanner.GenerateAsync, ProposeBibleUpdateHandler): a stop at
                    // `max_output_tokens` would repeat the same truncation at the same cap, and a data object the
                    // judge refused as too large is built identically on the next attempt, so both are final on
                    // the first call. Any other refusal earns D7's one retry, which is the run's second paid
                    // dispatch and carries its own bound and guard check (the top of this loop).
                    if (attempt == 1 && !IdeaCallFaults.IsFinalOutput(invalid))
                    {
                        continue;
                    }

                    LogFailed(runId, IdeaCallFaults.InvalidOutputDetail(invalid));

                    return (JudgeOutcome.Degraded, []);

                case TextCallModerated<NoveltyV1> moderated:
                    // TL ruling #3 (2026-09-24): a refusal on the run's own paid call writes one
                    // moderation_events row with `stage = ideas`, the sibling of the per-candidate `brief`
                    // pre-check. The batch then degrades like any other refusal.
                    await IdeaCallFaults
                        .WriteRefusalAsync(moderationEvents, run, moderated.Categories)
                        .ConfigureAwait(false);
                    LogFailed(runId, IdeaCallFaults.ProviderRefused);

                    return (JudgeOutcome.Degraded, []);

                case TextCallFundsExhausted<NoveltyV1>:
                    // The only judge outcome that stops the run: the account cannot pay for anything more.
                    LogFailed(runId, IdeaCallFaults.FundsExhausted);

                    return IdeaErrors.ProviderFundsExhausted;

                case TextCallUnavailable<NoveltyV1>:
                    LogFailed(runId, IdeaCallFaults.ProviderUnavailable);

                    return (JudgeOutcome.Degraded, []);

                case TextCallProviderError<NoveltyV1> error:
                    LogFailed(runId, error.Status == 0 ? IdeaCallFaults.Unexpected : IdeaCallFaults.ProviderError);

                    return (JudgeOutcome.Degraded, []);

                default:
                    // D7's unmodelled failure is a degraded judge, never a crash of the caller's job.
                    LogFailed(runId, IdeaCallFaults.UnmappedOutcome);

                    return (JudgeOutcome.Degraded, []);
            }
        }
    }

    /// <summary>
    /// Maps the answer's rows onto the batch, defensively: the 0-based <c>index</c> of a row is the candidate's
    /// position, a row outside the batch is ignored, a repeated index keeps the first row, and a candidate no row
    /// mentions keeps a null judge. One score row can therefore never cost another candidate its verdict, and a
    /// model's arithmetic can never drop a candidate (Decision 10).
    /// </summary>
    /// <param name="rows">The answer's rows, in the model's order.</param>
    /// <param name="corpus">The corpus the 1-based overlap index resolves against.</param>
    /// <param name="signals">The batch's signals, written in place.</param>
    private void ApplyJudgeRows(
        IReadOnlyList<NoveltyScoreV1> rows,
        NoveltyCorpus corpus,
        NoveltySignals[] signals)
    {
        var judged = new NoveltyJudge?[signals.Length];

        foreach (var row in rows)
        {
            if (row.Index < 0 || row.Index >= judged.Length || judged[row.Index] is not null)
            {
                continue;
            }

            judged[row.Index] = Map(row, corpus);
        }

        for (var index = 0; index < judged.Length; index++)
        {
            if (judged[index] is not { } judge)
            {
                continue;
            }

            signals[index] = signals[index] with { Judge = judge };
            metrics.NoveltyJudge(judge.Score);
        }
    }

    /// <summary>Maps one answer row to the stored judge record, clamping and sanitising what it carries.</summary>
    /// <param name="row">The answer's row.</param>
    /// <param name="corpus">The corpus the overlap index resolves against.</param>
    /// <returns>The judge record of that candidate.</returns>
    private static NoveltyJudge Map(NoveltyScoreV1 row, NoveltyCorpus corpus) =>
        new(
            // The schema bounds both scores to 0…1, but the column is numeric(5,4) and a provider that ignored
            // its own schema must not fail the run at save time.
            IdeaSignals.Clamp01(row.Score),
            IdeaSignals.Clamp01(row.Fit),
            IdeaSignals.Clamp01(row.HookStrength),
            ResolveEpisodeId(row.MaxOverlapEpisodeId, corpus),
            Reason(row.Reason));

    /// <summary>
    /// Resolves the judge's overlap reference — the <b>1-based</b> index of a past episode as a decimal string
    /// (D6/F12: models garble UUIDs) — against the corpus that was actually sent.
    /// </summary>
    /// <param name="index">The answer's value, or <see langword="null"/>.</param>
    /// <param name="corpus">The corpus the index counts into.</param>
    /// <returns>The episode's id, or <see langword="null"/> when the value names no episode of the corpus.</returns>
    private static Guid? ResolveEpisodeId(string? index, NoveltyCorpus corpus)
    {
        if (string.IsNullOrWhiteSpace(index)
            || !int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position)
            || position < 1
            || position > corpus.Episodes.Count)
        {
            // Out of range, non-numeric or garbled: the score still counts, the reference does not. The stored
            // document says `null` rather than naming an episode the judge never saw.
            return null;
        }

        return corpus.Episodes[position - 1].EpisodeId;
    }

    /// <summary>
    /// Turns the judge's justification into storable server-side data: control characters and lone surrogates
    /// replaced (a raw NUL or a lone surrogate fails the save), the Unicode format characters dropped, then
    /// trimmed and cut to <see cref="ReasonMaxChars"/> without splitting a surrogate pair.
    /// </summary>
    /// <param name="reason">The model's reason.</param>
    /// <returns>The text the <c>novelty</c> document carries.</returns>
    private static string Reason(string? reason)
    {
        var cleaned = DropFormats(TextSafety.Clean(reason)).Trim();
        if (cleaned.Length <= ReasonMaxChars)
        {
            return cleaned;
        }

        var length = char.IsHighSurrogate(cleaned[ReasonMaxChars - 1]) ? ReasonMaxChars - 1 : ReasonMaxChars;

        return cleaned[..length];
    }

    /// <summary>
    /// Drops the Unicode format characters (<see cref="UnicodeCategory.Format"/>) of a stored text: the bidi
    /// overrides and isolates (U+202E and its family), the zero-width space and joiner characters, and the tag
    /// characters of the astral plane (U+E0000…U+E007F) that can carry a hidden instruction inside text a model
    /// or an owner wrote. The walk is by <see cref="Rune"/>, so a character outside the BMP is judged as one
    /// character rather than as its surrogate pair.
    /// </summary>
    /// <param name="text">The text to clean; it has already been through <see cref="TextSafety.Clean"/>.</param>
    /// <returns>The same instance when nothing was dropped.</returns>
    private static string DropFormats(string text)
    {
        StringBuilder? builder = null;
        var keptFrom = 0;
        var walked = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.Utf16SequenceLength;

            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                // The text kept so far is copied in before the dropped character, so the builder holds the whole
                // surviving text rather than only its tail.
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, keptFrom, walked - keptFrom);
                keptFrom = walked + length;
            }

            walked += length;
        }

        return builder is null ? text : builder.Append(text, keptFrom, walked - keptFrom).ToString();
    }

    /// <summary>The corpus entries both passes measure against, each with the kind the stored document reports.</summary>
    /// <param name="corpus">The corpus the caller loaded.</param>
    /// <returns>The entries: the episodes first, then the pending ideas.</returns>
    private static List<LexicalCorpusEntry> BuildCorpusEntries(NoveltyCorpus corpus)
    {
        var entries = new List<LexicalCorpusEntry>(corpus.Episodes.Count + corpus.PendingIdeas.Count);
        entries.AddRange(corpus.Episodes.Select(episode =>
            new LexicalCorpusEntry(episode.EpisodeId, IdeaNoveltyKind.Episode, $"{episode.Title} {episode.Summary}")));
        entries.AddRange(corpus.PendingIdeas.Select(idea =>
            new LexicalCorpusEntry(idea.IdeaId, IdeaNoveltyKind.PendingIdea, $"{idea.Title} {idea.Logline}")));

        return entries;
    }

    /// <summary>The wire name of the series' language, the control value the judge's data object carries.</summary>
    /// <param name="lang">The series' language.</param>
    /// <returns>The snake_case name (<c>vi</c>, <c>en</c>).</returns>
    private static string WireLanguage(Language lang) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(lang.ToString());

    /// <summary>Keys the batch's signals by idea id.</summary>
    /// <param name="signals">The signals, in the batch's order.</param>
    /// <param name="batch">The batch, in the same order.</param>
    /// <param name="judgeUsed">Whether the batch's signals come from a judge answer.</param>
    /// <returns>The batch result.</returns>
    private static NoveltyBatchResult Build(
        NoveltySignals[] signals,
        IReadOnlyList<(Guid IdeaId, IdeaCandidate Candidate)> batch,
        bool judgeUsed)
    {
        var byIdeaId = new Dictionary<Guid, NoveltySignals>(batch.Count);
        for (var index = 0; index < batch.Count; index++)
        {
            byIdeaId[batch[index].IdeaId] = signals[index];
        }

        return new NoveltyBatchResult(byIdeaId, judgeUsed);
    }

    [LoggerMessage(
        EventId = 6369,
        Level = LogLevel.Warning,
        Message = "idea_novelty_invalid_output runId={RunId} attempt={Attempt} rules={Rules}")]
    private partial void LogInvalidOutput(Guid runId, int attempt, int rules);

    [LoggerMessage(
        EventId = 6370,
        Level = LogLevel.Warning,
        Message = "idea_novelty_failed runId={RunId} detail={Detail}")]
    private partial void LogFailed(Guid runId, string detail);

    [LoggerMessage(
        EventId = 6371,
        Level = LogLevel.Information,
        Message = "idea_novelty_batch runId={RunId} candidates={Candidates} episodes={Episodes} pendingIdeas={PendingIdeas} judgeUsed={JudgeUsed}")]
    private partial void LogPass(Guid runId, int candidates, int episodes, int pendingIdeas, bool judgeUsed);
}
