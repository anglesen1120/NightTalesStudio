using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Similarity;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The idea step of one autopilot slot (02.8.1-T2 plan §4 U1): read the series context, make the one paid ideas
/// call, read the corpus, run the one lexical pass and the batch's one judge call, pre-check the content policy,
/// score and reject every candidate, pick the best and store the whole batch in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pipeline (02.8.1-T2).</b> The batch is validated and its survivor rows are created <c>proposed</c>
/// before the corpus is read, because their ids are what the corpus excludes. Then, in order: the corpus of the
/// series' approved episodes and pending ideas (<see cref="IIdeaCorpusProvider"/>), one lexical shingle pass over
/// every candidate together with the batch's <b>one</b> judge call (<see cref="IIdeaNoveltyChecker"/>; no call at
/// all when every candidate is already rejected lexically), the content-policy pre-check
/// (<see cref="IIdeaPolicyChecker"/>: banned themes, the moderator and the true-crime research flag), the
/// per-candidate variety evaluation (<c>VarietyGuard</c>, 02.8.4-T1) and the weighted score of every candidate
/// (<c>IdeaScorer</c>). The pick is the highest-scoring candidate no rule
/// rejected, or <c>ideas_no_idea</c> carrying the most common rejection reason when every candidate was rejected.
/// Every row keeps its measurements whether it won or lost, and the whole batch is stored with one
/// <see cref="IAppDbContext.SaveChangesAsync"/>. The caller then marks the pick used (<c>MarkUsedAsync</c>) when
/// the episode consumes it.
/// </para>
/// <para>
/// <b>The variety step (02.8.4-T1).</b> Its input is the series' approved-episode structure history, which
/// <see cref="IIdeaContextProvider"/> has already read once into <c>IdeaContext.History</c> (newest first): the
/// planner assesses the window <b>before</b> the paid call, so the guard's suggestion travels in the idea prompt
/// as <c>required_tags</c>/<c>forbidden_tags</c> and nothing is read or computed twice. Per candidate the order
/// stays novelty → policy → variety → score → pick, so an earlier rule's reason always wins; a candidate whose
/// only variety finding is a tag-share hit is penalised (its penalty enters the score) and is never rejected, and
/// a block is the guard's own <c>variety_triple_repeat</c>/<c>variety_template_repeat</c> reason. Before the rows
/// are created, every survivor's three vocabulary tags are mapped through the shared
/// <c>StructureVocabMapper</c> (IDEA-S52-1), so the stored row and the guard read the same values.
/// </para>
/// <para>
/// <b>Ownership (task file [DECISION]).</b> The caller owns the <c>generation_runs</c> row — it creates it,
/// reserves the credit, checks the budget and records the outcome — and this planner never touches it. The
/// planner owns <c>episode_ideas</c> and writes it with one <see cref="IAppDbContext.SaveChangesAsync"/>: the
/// whole batch is stored or none of it is, in the scope's own unit of work. <b>The caller commits the run row
/// and holds no open transaction across this call</b>: a paid provider call of up to two attempts runs inside
/// it, so an open transaction here would pin a database connection and the run row's locks for the call's whole
/// duration. The paid call meters itself, one <c>usage_records</c> row per call (<c>operation = idea</c>),
/// because the generator sees the call's outcome (D14).
/// </para>
/// <para>
/// <b>Claims and retries.</b> D7 allows one retry, and only for a <c>TextCallInvalidOutput</c>, the outcome of
/// an answer the strict schema or the client refused. A stop at <c>max_output_tokens</c> would repeat the same
/// truncation, so it fails on the first call, and an <c>Ok</c> draft with no valid candidate fails
/// <c>ideas_invalid_output</c> without a second call, because that answer was complete and only its content was
/// refused. <c>attempt = 2</c> is the second and last call of the run, so a run makes at most two paid calls in
/// total, and the second one passes <see cref="IBudgetGuard"/> again: the caller's admission reserved one
/// dispatch, not two. Hangfire does not retry the caller's job (<c>Retries = 0</c>); a crash leaves the run to
/// the sweeper.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The series is resolved here and checked against the run's owner explicitly — both the
/// unknown series and another user's answer <c>not_found</c>, so nothing leaks through the difference — because a
/// job's scope may run under a system context whose query filter is off. The idea rows carry the series' owner
/// (<c>EpisodeIdea.CreateProposed</c>), so the D10 filter covers every later read.
/// </para>
/// <para>
/// <b>Logs (D19).</b> Ids, counts, the closed reason codes and the mapped outcome only: no idea text, no bible
/// text, no note and no provider message is ever passed to a logger.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model of the caller's scope: the series it reads and the ideas it writes.</param>
/// <param name="clock">The clock every stamp of a new idea row is taken from.</param>
/// <param name="contexts">Reads the series material of the batch: topic, language, tone, banned themes, bible, history and notes.</param>
/// <param name="generator">The one paid call of the run, its retry included; it meters its own usage row (D14).</param>
/// <param name="budgetGuard">The platform spend cap, re-checked before the run's second paid dispatch (D1: "before every paid dispatch").</param>
/// <param name="costEstimator">The worst case of one ideas call: the bound the retry's re-check is made with.</param>
/// <param name="moderationEvents">The one writer of <c>moderation_events</c> (D12): a refusal on this run writes one row.</param>
/// <param name="corpusProvider">Reads the series' approved episodes and pending ideas, the batch's own new rows excluded (02.8.1-T2).</param>
/// <param name="noveltyChecker">The lexical pass and the batch's one judge call (02.8.1-T2).</param>
/// <param name="policyChecker">The banned-theme, moderation and framing pre-check (02.8.1-T2).</param>
/// <param name="noveltyOptions">The novelty thresholds and corpus bound (<c>Autopilot:Ideas:Novelty</c>).</param>
/// <param name="weights">The score's weights (<c>Autopilot:Ideas:Weights</c>): the ranking the pick comes from.</param>
/// <param name="limits">The configured field limits <see cref="IdeaValidator"/> enforces.</param>
/// <param name="options">The configured batch size and its ceiling (<c>Autopilot:Ideas</c>).</param>
/// <param name="variety">The variety guard's window, cap and switch (<c>Autopilot:Variety</c>, 02.8.4-T1).</param>
/// <param name="prompts">
/// The D5 prompt catalog, read for the shared structure vocabulary
/// (<c>prompts/shared/structure-vocab.v1.json</c>) the guard's tag mapping uses (02.8.4-T1, IDEA-S52-1).
/// </param>
/// <param name="metrics">The feature's instruments: what happened to each candidate of the batch.</param>
/// <param name="logger">The outcome log of this run.</param>
public sealed partial class IdeaPlanner(
    IAppDbContext db,
    IClock clock,
    IIdeaContextProvider contexts,
    IIdeaGenerator generator,
    IBudgetGuard budgetGuard,
    IIdeaCostEstimator costEstimator,
    IModerationEventWriter moderationEvents,
    IIdeaCorpusProvider corpusProvider,
    IIdeaNoveltyChecker noveltyChecker,
    IIdeaPolicyChecker policyChecker,
    IdeaNoveltyOptions noveltyOptions,
    IdeaWeights weights,
    IdeaLimits limits,
    IdeaPlannerOptions options,
    VarietyOptions variety,
    IPromptCatalog prompts,
    IdeaMetrics metrics,
    ILogger<IdeaPlanner> logger) : IIdeaPlanner
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(
        Guid seriesId,
        DateTimeOffset slotAt,
        RunContext run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        // The owner is read from the row and compared here rather than trusted to the scope's query filter: a
        // job's scope may be a system context, whose filter is off (09 README D10). The run's own series is
        // checked too, so a caller that mixed up two series is refused instead of buying ideas for the wrong one.
        var owner = await db.Series
            .AsNoTracking()
            .Where(candidate => candidate.Id == seriesId)
            .Select(candidate => new { candidate.UserId })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (owner is null || owner.UserId != run.UserId || run.SeriesId != seriesId)
        {
            LogSeriesNotFound(run.GenerationRunId, seriesId);

            return IdeaErrors.SeriesNotFound;
        }

        var context = await contexts.GetAsync(seriesId, ct).ConfigureAwait(false);
        var language = Enum.Parse<Language>(context.Language, ignoreCase: true);

        // The variety guard's window and steering (02.8.4-T1), computed ONCE here — before the one paid call,
        // because the suggestion it produces is an input of the prompt, and it costs no money and reads no text.
        // The history is the list the context provider has already read (newest first, `IdeaContext.History`), so
        // the guard needs no second query; it takes its own WindowK/TemplateWindowK out of the head of it.
        var varietyWindow = context.History
            .Take(Math.Max(variety.WindowK, variety.TemplateWindowK))
            .ToArray();
        var assessment = VarietyGuard.AssessBatch(varietyWindow, variety);

        // IDEA-S52-1: the tags of the survivors are mapped onto the topic's own vocabulary with the shared
        // 02.2.1-T1 mapper and the shared vocabulary file (one reader: the D5 prompt catalog) before any row is
        // created, so what is stored is what the guard compares and what the writer prompt calls a vocabulary
        // value. `setting` is a free string and is not part of that set.
        var rules = new StoryRules
        {
            Language = language,
            StructureVocab = IdeaStructureMapper.Vocabulary(
                prompts.GetShared(PromptFile.StructureVocab, language),
                context.Topic,
                language),
        };

        // The prompt's continuity memory is the bible's own summaries; the history titles stand in for them only
        // when the bible carries none, so the same text is never paid for twice (task file [DECISION]). The test
        // is the summaries and not the snapshot itself (C5): a young series' bible holds a persona, a character or
        // a rule long before its first approved episode is summarised, and that snapshot — the normal state of a
        // series at this stage — would otherwise send no continuity memory at all.
        var recentEpisodes = new List<EpisodeSummaryRef>();
        if (context.Bible is null || context.Bible.EpisodeSummaries.Count == 0)
        {
            recentEpisodes.AddRange(
                context.History.Select(episode => new EpisodeSummaryRef(episode.Title, null)));
        }

        var request = new IdeaGenerationRequest(
            run,
            context.Language,
            context.Topic,
            context.Tone,
            context.BannedThemes,
            context.Bible,
            recentEpisodes,
            // An all-empty steering block is sent as `null` rather than as empty members, so the data object
            // omits the key entirely and the prompt's "absent means unconstrained" rule holds (02.8.4-T1).
            RequiredTags: RequiredOrNull(assessment.SuggestedRequired),
            ForbiddenTags: ForbiddenOrNull(assessment.SuggestedForbidden),
            context.ResearchNotes,
            EffectiveCount(context.Count));

        var generated = await GenerateAsync(request, ct).ConfigureAwait(false);
        if (generated.IsError)
        {
            return generated.Errors;
        }

        var batch = generated.Value;
        var survivors = SelectSurvivors(batch, rules, run.GenerationRunId);
        if (survivors.Count == 0)
        {
            LogNoValidCandidate(run.GenerationRunId, seriesId, batch.Count);

            return IdeaErrors.InvalidOutput;
        }

        // The batch's rows are created BEFORE the corpus is read, because their ids are what the corpus must
        // exclude: they are `proposed` rows of this very run, and a corpus that carried them would make every
        // candidate its own closest neighbour. They join the context only at the end, so a run that stops here
        // stores nothing.
        var ideas = new List<EpisodeIdea>(survivors.Count);
        var slots = new List<(Guid IdeaId, IdeaCandidate Candidate)>(survivors.Count);
        foreach (var candidate in survivors)
        {
            var idea = EpisodeIdea.CreateProposed(seriesId, run.UserId, run.GenerationRunId, candidate, clock);
            ideas.Add(idea);
            slots.Add((idea.Id, candidate));
        }

        var corpus = await corpusProvider
            .GetAsync(seriesId, [.. ideas.Select(idea => idea.Id)], ct)
            .ConfigureAwait(false);

        var measured = await noveltyChecker
            .CheckAsync(slots, corpus, language, context.Topic, run, ct)
            .ConfigureAwait(false);
        if (measured.IsError)
        {
            return measured.Errors;
        }

        var verdicts = await policyChecker
            .CheckAsync(slots, new IdeaPolicyContext(context.Topic, context.BannedThemes), run, ct)
            .ConfigureAwait(false);

        // The variety step 02.8.4-T1 owns: novelty, then the policy pre-check, then the variety guard's per
        // candidate verdict, then the score. Every candidate of the batch is scored and stored, rejected or not,
        // because the run log shows the whole pool and the calibration histograms need the readings of the ones
        // that lost.
        var novelty = measured.Value.ByIdeaId;

        // A batch whose answer carried no usable row at all has no measured candidate to protect, so every
        // candidate of it stays on the whole-batch neutrals (LexicalOnly, fit 1.0): that is today's behaviour and
        // its ranking is unaffected either way. The neutral fit of Unjudged applies only beside a measured
        // candidate (F6/S3).
        var anyJudged = novelty.Values.Any(signal => signal.Judge is not null);
        var signals = new IdeaSignals[slots.Count];
        var details = new IdeaNoveltyDetail[slots.Count];
        var scores = new decimal[slots.Count];
        var reasons = new string?[slots.Count];
        var eligible = new decimal?[slots.Count];
        var research = new bool[slots.Count];
        var decisions = new VarietyDecision[slots.Count];

        for (var index = 0; index < slots.Count; index++)
        {
            var signal = novelty[slots[index].IdeaId];
            var verdict = verdicts.ByIdeaId[slots[index].IdeaId];
            var judge = signal.Judge;

            // The guard reads the candidate's mapped tags and beats against the window assessed before the paid
            // call (02.8.4-T1): a hard block (a triple or a template repeat) rejects it, a tag-share hit only
            // penalises it. `Autopilot:Variety:Enabled=false` answers the neutral decision — steering stays, no
            // rejection and no penalty.
            var decision = VarietyGuard.Evaluate(slots[index].Candidate, assessment, variety);
            decisions[index] = decision;

            // A candidate with no judge reading is neutral, but not identically so: a batch whose call degraded
            // lost the judge for every candidate together, so it keeps LexicalOnly's fit of 1.0 and its own
            // ranking is unaffected; a batch the judge did answer, but whose answer carried no usable row for
            // this candidate, must not hand that candidate the generous 1.0 — fit is the score's second-largest
            // weight, and an unmeasured 1.0 would let it outrank candidates the judge did measure on the very
            // component it never answered about. That one candidate takes the neutral 0.5 instead (F6/S3).
            signals[index] = judge is not null
                ? new IdeaSignals(signal.LexicalScore, judge.Score, judge.Fit, judge.HookStrength, decision.Penalty)
                : anyJudged
                    ? IdeaSignals.Unjudged(signal.LexicalScore, decision.Penalty)
                    : IdeaSignals.LexicalOnly(signal.LexicalScore, decision.Penalty);
            details[index] = new IdeaNoveltyDetail(
                new IdeaNoveltyLexical(
                    signal.LexicalScore,
                    OverlapKind(signal.LexicalOverlapId, corpus),
                    signal.LexicalOverlapId),
                judge is null
                    ? null
                    : new IdeaNoveltyJudge(
                        judge.Score,
                        judge.Fit,
                        judge.HookStrength,
                        judge.Reason,
                        judge.MaxOverlapEpisodeId),
                new IdeaNoveltyCorpus(
                    corpus.Episodes.Count,
                    corpus.PendingIdeas.Count,
                    SimilarityOptions.NFor(language)));
            research[index] = verdict.RequiresResearch;
            scores[index] = IdeaScorer.Score(signals[index], weights);

            // The two raw readings decide the rejection, never the weighted score: the lexical ceiling first (it
            // is free and it is what the whole batch shares), then the judge's own score, then the policy's
            // verdict, and last the variety guard's block — so an earlier rule's reason always wins and a
            // candidate whose only variety finding is the tag-share cap is scored rather than rejected. A
            // candidate that survives all four is eligible for the pick.
            reasons[index] = signal.LexicalScore > noveltyOptions.EffectiveLexicalMax
                ? IdeaRejectionReason.NoveltyLexical
                : judge is not null && judge.Score > noveltyOptions.EffectiveJudgeMax
                    ? IdeaRejectionReason.NoveltyJudge
                    : verdict.RejectReason ?? decision.RejectReason;
            eligible[index] = reasons[index] is null ? scores[index] : null;
        }

        var winner = IdeaScorer.PickIndex(eligible);

        // The candidates the guard's own rules actually rejected, counted per rule: a candidate whose earlier rule
        // (novelty or the policy) also named a reason keeps that reason, so the guard is credited only for its own
        // blocks, and the reason it is credited under is one of the closed `IdeaRejectionReason` constants — never
        // text (D19).
        var varietyBlocks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reason in reasons)
        {
            if (reason is not null && IdeaMetrics.VarietyRuleNames.Contains(reason))
            {
                varietyBlocks[reason] = varietyBlocks.GetValueOrDefault(reason) + 1;
            }
        }

        // The complement of that count: a candidate the cap penalised *without* blocking, its pick included. A
        // blocked candidate keeps its soft violations, so counting every positive penalty would count it in both
        // `variety_penalty_total` and `variety_rejections_total`; only a candidate no rule rejected is counted.
        var varietyPenalties = decisions.Count((decision, index) => reasons[index] is null && decision.Penalty > 0m);

        for (var index = 0; index < ideas.Count; index++)
        {
            // Every row keeps its measurements, whatever its status: the components the pick was ranked on and
            // the novelty document the calibration reader opens.
            ideas[index].SetSignals(scores[index], Similarity(signals[index]), details[index], clock);

            if (index == winner)
            {
                ideas[index].MarkPicked(scores[index], clock);
                continue;
            }

            // A candidate that broke no rule lost the ranking: `not_best` is the reason the run log shows for it.
            var reason = reasons[index] ?? IdeaRejectionReason.NotBest;
            ideas[index].MarkRejected(reason, clock);
            metrics.Candidates(reason, 1);
        }

        foreach (var (rule, blocked) in varietyBlocks)
        {
            metrics.VarietyRejections(rule, blocked);
        }

        if (varietyPenalties > 0)
        {
            metrics.VarietyPenalties(varietyPenalties);
        }

        if (varietyBlocks.Count > 0 || varietyPenalties > 0)
        {
            // Counts and closed codes only (D19): the window the guard read, the candidates it penalised and the
            // candidates its rules rejected, with the rule named when every block shares one ("-" otherwise).
            var blockedRules = varietyBlocks.Keys.Order(StringComparer.Ordinal).ToArray();
            var blockedTotal = varietyBlocks.Values.Sum();
            LogVariety(
                run.GenerationRunId,
                seriesId,
                assessment.WindowSize,
                varietyPenalties,
                blockedTotal,
                blockedRules.Length == 1 ? blockedRules[0] : "-");
        }

        if (winner is not null)
        {
            metrics.Candidates(IdeaMetrics.PickedOutcome, 1);
        }

        db.EpisodeIdeas.AddRange(ideas);

        // One SaveChanges is one transaction: the batch is stored whole or not at all. The caller has already
        // committed its run row before this call (the contract on ProposeAndPickAsync), so this write joins no
        // open transaction and is this step's only unit of work. A batch every rule rejected is stored too: the
        // run log shows the whole pool with each candidate's reason.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (winner is null)
        {
            // The run has no idea to offer. The most common reason travels with the error (deterministic: highest
            // count, then alphabetical) so 02.8.3 records and alerts on a code rather than on nothing.
            var commonReason = IdeaRejectionSummary.MostCommon(reasons.OfType<string>())!;
            LogNoIdea(run.GenerationRunId, seriesId, survivors.Count, commonReason);

            return IdeaErrors.NoIdea(commonReason);
        }

        var picked = survivors[winner.Value];
        var pick = new IdeaPick(
            ideas[winner.Value].Id,
            picked.Title,
            picked.Logline,
            picked.Hook,
            picked.Setting,
            picked.Structure,
            // T1's topic rule stays (IDEA-S52-6) and the framing the pre-check measured joins it: a true-crime
            // series always needs research, whatever the idea says.
            RequiresResearch: research[winner.Value]
                || string.Equals(context.Topic, IdeaPolicyContext.TrueCrimeTopic, StringComparison.Ordinal),
            Score: scores[winner.Value],
            Similarity: Similarity(signals[winner.Value]));

        LogPicked(run.GenerationRunId, seriesId, pick.IdeaId, survivors.Count, batch.Count);

        return pick;
    }

    /// <summary>
    /// The similarity the run log shows: the larger of the two raw readings, which is the reading the stricter
    /// threshold answers to (plan §3.4, task Design "Novelty").
    /// </summary>
    /// <param name="signals">The candidate's measured signals.</param>
    /// <returns><c>max(lexical, judge)</c>, 0…1.</returns>
    private static decimal Similarity(IdeaSignals signals) =>
        Math.Max(signals.LexicalScore, signals.JudgeScore);

    /// <summary>
    /// The corpus kind of the row the lexical pass named, as the stored document reports it: an approved episode
    /// or a pending idea of the same series.
    /// </summary>
    /// <param name="overlapId">The corpus row's id, or <see langword="null"/> when nothing overlapped.</param>
    /// <param name="corpus">The corpus that was measured.</param>
    /// <returns><c>episode</c>, <c>pending_idea</c>, or <see langword="null"/>.</returns>
    private static string? OverlapKind(Guid? overlapId, NoveltyCorpus corpus)
    {
        if (overlapId is not { } id)
        {
            return null;
        }

        return corpus.Episodes.Any(episode => episode.EpisodeId == id)
            ? IdeaNoveltyKind.Episode
            : IdeaNoveltyKind.PendingIdea;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct)
    {
        // The tenant filter of the caller's scope is what hides another user's idea (09 README D10). The row is
        // read as a guard (and for its series), not as the write: the transition below is one atomic UPDATE.
        var idea = await db.EpisodeIdeas
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == ideaId, ct)
            .ConfigureAwait(false);
        if (idea is null)
        {
            LogIdeaNotFound(ideaId);

            return IdeaErrors.IdeaNotFound;
        }

        // SEC-4: the composite key ties the link to the owner, not to the series, so an episode of another series
        // of the same owner would pass it and corrupt this series' corpus — the run log and 02.8.4's guard both
        // read ideas per series. The episode is therefore resolved inside the idea's own series, through the
        // scope's tenant-filtered model: the unknown, another user's and another series' episode all answer the
        // same not_found, so this check is no oracle (09 README D10). The same-owner key stays as the last line
        // of defence for the rows that race with this read.
        var episodeInSeries = await db.Episodes
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == episodeId && candidate.SeriesId == idea.SeriesId, ct)
            .ConfigureAwait(false);
        if (!episodeInSeries)
        {
            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeNotFound.Code);

            return IdeaErrors.EpisodeNotFound;
        }

        try
        {
            // The transition is ONE atomic conditional update (IDEA-S52-3, contract Decision 8): the WHERE clause
            // is the domain rule — `MarkUsed`'s `picked` requirement plus the `episode_id IS NULL` guard the
            // partial unique index enforces one level down — and the affected row count is the answer. Two
            // concurrent calls with different episodes therefore cannot both win, and nothing is tracked, so a
            // refused flush leaves nothing for the caller's own write to replay. The precedent is the stale-run
            // claim of the story generation pipeline (<c>StoryGenerationPipeline</c>).
            var affected = await db.EpisodeIdeas
                .Where(candidate => candidate.Id == ideaId
                    && candidate.Status == IdeaStatus.Picked
                    && candidate.EpisodeId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.Status, IdeaStatus.Used)
                        .SetProperty(candidate => candidate.EpisodeId, episodeId)
                        .SetProperty(candidate => candidate.UpdatedAt, clock.UtcNow),
                    ct)
                .ConfigureAwait(false);
            if (affected == 1)
            {
                return Result.Success;
            }

            // 0 rows: the row moved under this call — it was rejected, or another call linked it first — or it
            // vanished. The re-read is what tells those apart, and both answers stay inside the codes this
            // operation already returns (09 README D10): a row that is gone is the same not_found a lookup gives,
            // and a row that can no longer be used is the 409 the state machine spells.
            var status = await db.EpisodeIdeas
                .AsNoTracking()
                .Where(candidate => candidate.Id == ideaId)
                .Select(candidate => (IdeaStatus?)candidate.Status)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (status is null)
            {
                LogIdeaNotFound(ideaId);

                return IdeaErrors.IdeaNotFound;
            }

            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeAlreadyUsed.Code);

            return IdeaErrors.EpisodeAlreadyUsed;
        }
        catch (Exception exception) when (EpisodeIdeaConflict.IsEpisodeUniqueViolation(exception))
        {
            // UX_episode_ideas_episode: the episode already has an idea. The same 409 class as a refused
            // transition, answered instead of reaching the caller's job as a 500. The type is Exception because the
            // ExecuteUpdate above raises the provider's refusal raw, without a DbUpdateException around it.
            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeAlreadyUsed.Code);

            return IdeaErrors.EpisodeAlreadyUsed;
        }
        catch (Exception exception) when (EpisodeIdeaConflict.IsEpisodeForeignKeyViolation(exception))
        {
            // The episode is not this idea's owner's — another user's row, or one that was deleted in between — so
            // the same-owner key refused the link instead of it leaking across tenants. That is the answer a read
            // of that episode would have given (09 README D10), not a conflict and not a 500.
            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeNotFound.Code);

            return IdeaErrors.EpisodeNotFound;
        }
    }

    /// <summary>
    /// Makes the one call of the run, with the single D7 retry an invalid output earns: two attempts at most, the
    /// second under <c>attempt = 2</c>, which is what the D7 correlation id
    /// (<c>{generationRunId}:ideas:{attempt}</c>) and the run's attempt counter read.
    /// </summary>
    /// <param name="request">The batch's inputs; its run's attempt is rewritten per call.</param>
    /// <param name="ct">Cancels the calls.</param>
    /// <returns>The model's candidates, or the mapped outcome the caller records.</returns>
    private async ValueTask<ErrorOr<IReadOnlyList<IdeaCandidate>>> GenerateAsync(
        IdeaGenerationRequest request,
        CancellationToken ct)
    {
        var runId = request.Run.GenerationRunId;

        for (var attempt = 1; ; attempt++)
        {
            var result = await generator
                .GenerateAsync(request with { Run = request.Run with { Attempt = attempt } }, ct)
                .ConfigureAwait(false);

            switch (result)
            {
                case TextCallOk<IdeasV1> ok:
                    return ErrorOrFactory.From(ok.Draft.ToCandidates());

                case TextCallInvalidOutput<IdeasV1> invalid:
                    LogInvalidOutput(runId, attempt, invalid.RuleIds.Count);

                    // C2, the precedent's rule (ProposeBibleUpdateHandler): a stop at `max_output_tokens` would
                    // repeat the same truncation at the same cap, so it is final on the first call.
                    var truncated = IdeaCallFaults.IsTruncated(invalid);
                    if (attempt == 1 && !truncated)
                    {
                        // D7's one retry, which is the run's second paid dispatch: the caller's admission reserved
                        // the first one, so the platform cap is re-checked here with this call's own worst case
                        // before the second call is bought (D1: the guard runs before every paid dispatch). The
                        // run's own bound is excluded exactly as the admission-time check excludes it
                        // (ProposeBibleUpdateHandler): the reservation already covers it, so only this dispatch's
                        // worst case is added, and naming the run reads no generation_runs row.
                        var bound = await costEstimator
                            .WorstCaseAsync(request.Run.PriceVersion, ct)
                            .ConfigureAwait(false);
                        if (bound.IsError)
                        {
                            // No usable price version or no shipped prompt: fail closed, never dispatch unbounded.
                            LogFailed(runId, bound.FirstError.Code);

                            return bound.Errors;
                        }

                        var allowed = await budgetGuard
                            .CheckAsync(new CostBound(bound.Value.WorstCaseUsd), ct, request.Run.GenerationRunId)
                            .ConfigureAwait(false);
                        if (allowed.IsError)
                        {
                            LogFailed(runId, IdeaCallFaults.SpendCapReached);

                            return IdeaErrors.SpendCapReached;
                        }

                        continue;
                    }

                    LogFailed(
                        runId,
                        truncated ? IdeaCallFaults.InvalidOutputMaxTokens : IdeaCallFaults.InvalidOutputTwice);

                    return IdeaErrors.InvalidOutput;

                case TextCallModerated<IdeasV1> moderated:
                    // TL ruling #3 (2026-09-24): a refusal on the run's own paid call writes one
                    // moderation_events row with `stage = kind` — distinct from 02.8.1-T2's per-candidate
                    // pre-check, which writes `stage = brief`.
                    await IdeaCallFaults
                        .WriteRefusalAsync(moderationEvents, request.Run, moderated.Categories)
                        .ConfigureAwait(false);
                    LogFailed(runId, IdeaCallFaults.ProviderRefused);

                    return IdeaErrors.Moderated;

                case TextCallFundsExhausted<IdeasV1>:
                    LogFailed(runId, IdeaCallFaults.FundsExhausted);

                    return IdeaErrors.ProviderFundsExhausted;

                case TextCallUnavailable<IdeasV1>:
                    LogFailed(runId, IdeaCallFaults.ProviderUnavailable);

                    return IdeaErrors.GenerationFailed;

                case TextCallProviderError<IdeasV1> error:
                    LogFailed(runId, error.Status == 0 ? IdeaCallFaults.Unexpected : IdeaCallFaults.ProviderError);

                    return IdeaErrors.GenerationFailed;

                default:
                    // D7's unmodelled failure is still a failed generation, never a crash of the caller's job.
                    LogFailed(runId, IdeaCallFaults.UnmappedOutcome);

                    return IdeaErrors.GenerationFailed;
            }
        }
    }

    /// <summary>
    /// Applies the four rules of plan §4 U1 step 4 to one model batch, in the plan's order: everything beyond
    /// <c>Autopilot:Ideas:MaxCount</c> is dropped, then every candidate <see cref="IdeaValidator"/> refuses, then
    /// every candidate whose structure tags the shared D3 mapper (02.8.4-T1, IDEA-S52-1) cannot store, then every
    /// candidate whose normalised title repeats an earlier survivor (the later one is dropped with reason
    /// <c>duplicate</c>).
    /// </summary>
    /// <param name="batch">The model's candidates, in its own order.</param>
    /// <param name="rules">
    /// The rules carrying the topic's structure vocabulary: the mapper maps the survivors' tags with them before
    /// anything is stored or scored (02.8.4-T1).
    /// </param>
    /// <param name="runId">The run, for the log line.</param>
    /// <returns>The survivors, in the batch's order; empty when no candidate passed.</returns>
    private List<IdeaCandidate> SelectSurvivors(IReadOnlyList<IdeaCandidate> batch, StoryRules rules, Guid runId)
    {
        var maxCount = EffectiveMaxCount();
        var survivors = new List<IdeaCandidate>(Math.Min(batch.Count, maxCount));
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var invalid = 0;
        var duplicate = 0;

        for (var index = 0; index < batch.Count; index++)
        {
            var candidate = batch[index];

            // The failure table counts an over-limit candidate as dropped-invalid: the cap is applied before the
            // validator so a batch ten times its size cannot reach it.
            if (index >= maxCount || IdeaValidator.Validate(candidate, limits).Count > 0)
            {
                invalid++;
                continue;
            }

            // IDEA-S52-1: a value outside the topic's vocabulary is stored as `other:<text>` by the shared mapper,
            // which is what the prompt promises the model; the advisory issue that mapping raises is expected and
            // does not drop anything. Only a blocking text issue (over 40 characters, markup or a URL) does, and
            // it is a safety net: `IdeaValidator` has just refused the same shapes, so a candidate can only reach
            // it through a disagreement between the two rule sets.
            var mapping = IdeaStructureMapper.Map(candidate, rules);
            if (mapping.IsBlocked)
            {
                invalid++;
                continue;
            }

            candidate = mapping.Candidate;

            if (!titles.Add(IdeaTitleKey.Normalise(candidate.Title)))
            {
                duplicate++;
                continue;
            }

            survivors.Add(candidate);
        }

        if (invalid > 0)
        {
            metrics.Candidates(IdeaRejectionReason.Invalid, invalid);
        }

        if (duplicate > 0)
        {
            metrics.Candidates(IdeaRejectionReason.Duplicate, duplicate);
        }

        LogBatch(runId, batch.Count, survivors.Count, invalid, duplicate);

        return survivors;
    }

    [LoggerMessage(
        EventId = 6368,
        Level = LogLevel.Warning,
        Message = "idea_planner_no_idea runId={RunId} seriesId={SeriesId} candidates={Candidates} reason={Reason}")]
    private partial void LogNoIdea(Guid runId, Guid seriesId, int candidates, string reason);

    /// <summary>
    /// The number of ideas this run asks for: the context's count, clamped to <c>1…MaxCount</c>. A context that
    /// reports no usable count (a mis-bound provider) falls back to the configured default rather than buying one
    /// idea.
    /// </summary>
    /// <param name="fromContext">The count the context provider read from the same section.</param>
    /// <returns>The count the request carries.</returns>
    private int EffectiveCount(int fromContext)
    {
        var requested = fromContext > 0
            ? fromContext
            : options.Count > 0 ? options.Count : IdeaPlannerOptions.DefaultCount;

        return Math.Clamp(requested, 1, EffectiveMaxCount());
    }

    /// <summary>
    /// The ceiling a batch is cut to (<c>Autopilot:Ideas:MaxCount</c>), never below one: a mis-configured zero
    /// must not turn every batch into an empty one.
    /// </summary>
    /// <returns>The ceiling.</returns>
    private int EffectiveMaxCount() =>
        options.MaxCount > 0 ? options.MaxCount : IdeaPlannerOptions.DefaultMaxCount;

    /// <summary>
    /// The guard's required values as the request carries them: an unconstrained dimension is already
    /// <see langword="null"/> inside the record, and a block that constrains nothing at all is dropped so the
    /// idea data object omits <c>required_tags</c> rather than sending an empty member (02.8.4-T1).
    /// </summary>
    /// <param name="required">The guard's suggested requirements.</param>
    /// <returns>The value to send, or <see langword="null"/> when the block constrains nothing.</returns>
    private static StructureRequirements? RequiredOrNull(StructureRequirements required) =>
        required == StructureRequirements.None ? null : required;

    /// <summary>
    /// The guard's forbidden values as the request carries them: a block with no forbidden value in any dimension
    /// is dropped so the idea data object omits <c>forbidden_tags</c> (02.8.4-T1).
    /// </summary>
    /// <param name="forbidden">The guard's suggested forbidden values.</param>
    /// <returns>The value to send, or <see langword="null"/> when nothing is forbidden.</returns>
    private static StructureForbidden? ForbiddenOrNull(StructureForbidden forbidden) =>
        forbidden.IsEmpty ? null : forbidden;

    [LoggerMessage(
        EventId = 6360,
        Level = LogLevel.Warning,
        Message = "idea_planner_series_not_found runId={RunId} seriesId={SeriesId}")]
    private partial void LogSeriesNotFound(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6361,
        Level = LogLevel.Warning,
        Message = "idea_planner_invalid_output runId={RunId} attempt={Attempt} rules={Rules}")]
    private partial void LogInvalidOutput(Guid runId, int attempt, int rules);

    [LoggerMessage(
        EventId = 6362,
        Level = LogLevel.Information,
        Message = "idea_planner_batch runId={RunId} candidates={Candidates} survivors={Survivors} invalid={Invalid} duplicate={Duplicate}")]
    private partial void LogBatch(Guid runId, int candidates, int survivors, int invalid, int duplicate);

    [LoggerMessage(
        EventId = 6363,
        Level = LogLevel.Warning,
        Message = "idea_planner_no_valid_candidate runId={RunId} seriesId={SeriesId} candidates={Candidates}")]
    private partial void LogNoValidCandidate(Guid runId, Guid seriesId, int candidates);

    [LoggerMessage(
        EventId = 6364,
        Level = LogLevel.Information,
        Message = "idea_planner_picked runId={RunId} seriesId={SeriesId} ideaId={IdeaId} survivors={Survivors} candidates={Candidates}")]
    private partial void LogPicked(Guid runId, Guid seriesId, Guid ideaId, int survivors, int candidates);

    [LoggerMessage(
        EventId = 6365,
        Level = LogLevel.Warning,
        Message = "idea_planner_failed runId={RunId} detail={Detail}")]
    private partial void LogFailed(Guid runId, string detail);

    [LoggerMessage(
        EventId = 6366,
        Level = LogLevel.Warning,
        Message = "idea_planner_mark_used_idea_not_found ideaId={IdeaId}")]
    private partial void LogIdeaNotFound(Guid ideaId);

    [LoggerMessage(
        EventId = 6367,
        Level = LogLevel.Warning,
        Message = "idea_planner_mark_used_refused ideaId={IdeaId} code={Code}")]
    private partial void LogMarkUsedRefused(Guid ideaId, string code);

    /// <summary>
    /// One line of the variety step (02.8.4-T1, D19): the counts the guard produced and, when every block of the
    /// batch shares one, the closed rule code that blocked them.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="seriesId">The series.</param>
    /// <param name="window">How many history entries the assessment read.</param>
    /// <param name="penalties">How many candidates carried a positive penalty.</param>
    /// <param name="blocks">How many candidates the guard's own rules rejected.</param>
    /// <param name="rule">The shared block rule code, or <c>-</c> when the batch's blocks differ or there is none.</param>
    [LoggerMessage(
        EventId = 6374,
        Level = LogLevel.Information,
        Message = "idea_planner_variety runId={RunId} seriesId={SeriesId} window={Window} penalties={Penalties} blocks={Blocks} rule={Rule}")]
    private partial void LogVariety(Guid runId, Guid seriesId, int window, int penalties, int blocks, string rule);
}
