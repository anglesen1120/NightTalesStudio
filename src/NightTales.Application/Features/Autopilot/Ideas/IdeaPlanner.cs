using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The idea step of one autopilot slot (02.8.1-T1 plan §4 U1, steps 1, 3, 4, 5 and 10): read the series context,
/// make the paid call, validate the batch, persist every survivor as <c>proposed</c> and answer the first one as
/// the pick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope of T1.</b> The variety steering, the novelty check, the content-policy pre-check, the scoring and the
/// rejection of the losing candidates are 02.8.1-T2's and 02.8.4-T1's steps, so nothing here scores: the pick is
/// the first valid candidate in the model's order, and every survivor of the validity and duplicate rules is
/// stored <c>proposed</c>, including the ones T2 will later reject, because the run log shows the whole pool.
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
/// <param name="limits">The configured field limits <see cref="IdeaValidator"/> enforces.</param>
/// <param name="options">The configured batch size and its ceiling (<c>Autopilot:Ideas</c>).</param>
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
    IdeaLimits limits,
    IdeaPlannerOptions options,
    IdeaMetrics metrics,
    ILogger<IdeaPlanner> logger) : IIdeaPlanner
{
    /// <summary>
    /// The series topic whose ideas are angles rather than stories (F33), in the snake_case spelling the prompt's
    /// control keys use.
    /// </summary>
    private static readonly string TrueCrimeTopic =
        JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(Topic.TrueCrime));

    /// <summary>
    /// The score and the similarity 02.8.1-T1 answers: the conservative placeholder of a batch nothing has scored
    /// yet. 02.8.1-T2 replaces both with the weighted score and <c>max(lexical, judge)</c>.
    /// </summary>
    private const decimal Unscored = 0m;

    /// <summary>The write budget of the refusal evidence, which must survive a caller that gave up (class 3).</summary>
    private static readonly TimeSpan ModerationWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The failure detail of a provider that answered 5xx or timed out past its retry budget.</summary>
    private const string DetailProviderUnavailable = "provider_unavailable";

    /// <summary>The failure detail of a provider that refused the request with a non-retryable status.</summary>
    private const string DetailProviderError = "provider_error";

    /// <summary>The failure detail of a provider that failed before its call was dispatched.</summary>
    private const string DetailUnexpected = "unexpected_error";

    /// <summary>The failure detail of a provider that refused or filtered the call (D12's refusal).</summary>
    private const string DetailProviderRefused = "provider_refused";

    /// <summary>The failure detail of a provider account with no funds left (never metered, D7).</summary>
    private const string DetailFundsExhausted = "funds_exhausted";

    /// <summary>The failure detail of a second output the schema or the validator refused.</summary>
    private const string DetailInvalidOutputTwice = "invalid_output_twice";

    /// <summary>The failure detail of an output cut short by the ruled <c>max_output_tokens</c> (no retry: D7).</summary>
    private const string DetailInvalidOutputMaxTokens = "invalid_output_max_tokens";

    /// <summary>The failure detail of a retry the platform spend cap refused before dispatch (D1).</summary>
    private const string DetailSpendCapReached = "spend_cap_reached";

    /// <summary>The failure detail of a text-call outcome this table does not name (D7's unmodelled failure).</summary>
    private const string DetailUnmappedOutcome = "unmapped_outcome";

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
            RequiredTags: null,
            ForbiddenTags: null,
            context.ResearchNotes,
            EffectiveCount(context.Count));

        var generated = await GenerateAsync(request, ct).ConfigureAwait(false);
        if (generated.IsError)
        {
            return generated.Errors;
        }

        var batch = generated.Value;
        var survivors = SelectSurvivors(batch, run.GenerationRunId);
        if (survivors.Count == 0)
        {
            LogNoValidCandidate(run.GenerationRunId, seriesId, batch.Count);

            return IdeaErrors.InvalidOutput;
        }

        var ideas = new List<EpisodeIdea>(survivors.Count);
        foreach (var candidate in survivors)
        {
            ideas.Add(EpisodeIdea.CreateProposed(seriesId, run.UserId, run.GenerationRunId, candidate, clock));
        }

        db.EpisodeIdeas.AddRange(ideas);

        // One SaveChanges is one transaction: the batch is stored whole or not at all. The caller has already
        // committed its run row before this call (the contract on ProposeAndPickAsync), so this write joins no
        // open transaction and is this step's only unit of work.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        metrics.Candidates(IdeaMetrics.ProposedOutcome, ideas.Count);
        metrics.Candidates(IdeaMetrics.PickedOutcome, 1);

        // T1's pick: the first valid candidate, unscored (plan §4 U1 step 10 has the pick before 02.8.1-T2's
        // ranking exists). The row's own status stays `proposed`: only the scoring step picks a row.
        var pick = new IdeaPick(
            ideas[0].Id,
            survivors[0].Title,
            survivors[0].Logline,
            survivors[0].Hook,
            survivors[0].Setting,
            survivors[0].Structure,
            RequiresResearch: string.Equals(context.Topic, TrueCrimeTopic, StringComparison.Ordinal),
            Score: Unscored,
            Similarity: Unscored);

        LogPicked(run.GenerationRunId, seriesId, pick.IdeaId, survivors.Count, batch.Count);

        return pick;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct)
    {
        // The tenant filter of the caller's scope is what hides another user's idea (09 README D10); the row is
        // read tracked, because the transition below is its write.
        var idea = await db.EpisodeIdeas
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

        var marked = idea.MarkUsed(episodeId, clock);
        if (marked.IsError)
        {
            // Not picked (or already used): the 409 the Domain spells. Nothing is written, and no provider
            // constraint is reached, because the transition was refused before the update.
            LogMarkUsedRefused(ideaId, marked.FirstError.Code);

            return marked;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (EpisodeIdeaConflict.IsEpisodeUniqueViolation(exception))
        {
            // The episode already has an idea: UX_episode_ideas_episode refused the update, which is the same
            // 409 class as the refused transition one level up. The refusal is recovered before it is answered, so
            // the caller's own flush (the run outcome) cannot replay the UPDATE the constraint just rejected.
            await RecoverAsync(idea, ct).ConfigureAwait(false);
            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeAlreadyUsed.Code);

            return IdeaErrors.EpisodeAlreadyUsed;
        }
        catch (DbUpdateException exception) when (EpisodeIdeaConflict.IsEpisodeForeignKeyViolation(exception))
        {
            // The episode is not this idea's owner's — another user's row, or one that was deleted in between — so
            // the same-owner key refused the link instead of it leaking across tenants. That is the answer a read
            // of that episode would have given (09 README D10), not a conflict and not a 500.
            await RecoverAsync(idea, ct).ConfigureAwait(false);
            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeNotFound.Code);

            return IdeaErrors.EpisodeNotFound;
        }

        return Result.Success;
    }

    /// <summary>
    /// Resets the entry a refused flush left <c>Modified</c> to the row the database still holds, so the caller's
    /// own <c>SaveChanges</c> cannot replay the UPDATE the provider just refused.
    /// </summary>
    /// <remarks>
    /// <c>EntityEntry.ReloadAsync</c> is the recovery this layer already uses after a refused flush
    /// (<c>ProposeBibleUpdateHandler.FreshAsync</c>, <c>GenerateFactSheetPipeline.FreshAsync</c>): it re-reads the
    /// one entry and leaves every other tracked entity alone — clearing the tracker would drop the caller's pending
    /// work with it, and assigning <c>EntityEntry.State</c> is banned outside the admin feature (02.2.2 plan §3.2).
    /// A row that vanished in between leaves the entry detached, which the caller's flush then ignores.
    /// </remarks>
    /// <param name="idea">The idea whose update was refused.</param>
    /// <param name="ct">Cancels the re-read.</param>
    /// <returns>A task that completes when the entry matches the committed row again.</returns>
    private async ValueTask RecoverAsync(EpisodeIdea idea, CancellationToken ct) =>
        await Context.Entry(idea).ReloadAsync(ct).ConfigureAwait(false);

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
                    var truncated = invalid.RuleIds.Contains(TextCallInvalidOutput<IdeasV1>.MaxOutputTokensExceeded);
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
                            LogFailed(runId, DetailSpendCapReached);

                            return IdeaErrors.SpendCapReached;
                        }

                        continue;
                    }

                    LogFailed(runId, truncated ? DetailInvalidOutputMaxTokens : DetailInvalidOutputTwice);

                    return IdeaErrors.InvalidOutput;

                case TextCallModerated<IdeasV1> moderated:
                    // TL ruling #3 (2026-09-24): a refusal on the run's own paid call writes one
                    // moderation_events row with `stage = kind` — distinct from 02.8.1-T2's per-candidate
                    // pre-check, which writes `stage = brief`.
                    await WriteModerationEventAsync(request.Run, moderated.Categories).ConfigureAwait(false);
                    LogFailed(runId, DetailProviderRefused);

                    return IdeaErrors.Moderated;

                case TextCallFundsExhausted<IdeasV1>:
                    LogFailed(runId, DetailFundsExhausted);

                    return IdeaErrors.ProviderFundsExhausted;

                case TextCallUnavailable<IdeasV1>:
                    LogFailed(runId, DetailProviderUnavailable);

                    return IdeaErrors.GenerationFailed;

                case TextCallProviderError<IdeasV1> error:
                    LogFailed(runId, error.Status == 0 ? DetailUnexpected : DetailProviderError);

                    return IdeaErrors.GenerationFailed;

                default:
                    // D7's unmodelled failure is still a failed generation, never a crash of the caller's job.
                    LogFailed(runId, DetailUnmappedOutcome);

                    return IdeaErrors.GenerationFailed;
            }
        }
    }

    /// <summary>
    /// Applies the three rules of plan §4 U1 step 4 to one model batch, in the plan's order: everything beyond
    /// <c>Autopilot:Ideas:MaxCount</c> is dropped, then every candidate <see cref="IdeaValidator"/> refuses, then
    /// every candidate whose normalised title repeats an earlier survivor (the later one is dropped with reason
    /// <c>duplicate</c>).
    /// </summary>
    /// <param name="batch">The model's candidates, in its own order.</param>
    /// <param name="runId">The run, for the log line.</param>
    /// <returns>The survivors, in the batch's order; empty when no candidate passed.</returns>
    private List<IdeaCandidate> SelectSurvivors(IReadOnlyList<IdeaCandidate> batch, Guid runId)
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

    /// <summary>
    /// Writes the one <c>moderation_events</c> row of a refused run (D12, TL ruling #3), on its own budget and
    /// its own scope — the writer swallows its own failures, so the evidence never changes the run's outcome.
    /// </summary>
    /// <param name="run">The run the refusal belongs to; it is series-level, so the episode is null.</param>
    /// <param name="categories">The flagged categories when the provider named them; empty otherwise.</param>
    /// <returns>A task that completes when the row is saved or the failure is logged.</returns>
    private async ValueTask WriteModerationEventAsync(RunContext run, IReadOnlyList<string> categories)
    {
        using var terminal = new CancellationTokenSource(ModerationWriteTimeout);
        await moderationEvents
            .WriteAsync(
                run.UserId,
                run.EpisodeId,
                ModerationSource.OpenaiText,
                ModerationStage.Ideas,
                categories,
                // D19: the refusal is about our own prompt and the model's answer, and neither may be stored as
                // evidence text — the categories and the run are the whole record.
                ownText: null,
                terminal.Token)
            .ConfigureAwait(false);
    }

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
    /// The context of the run's tracked DbContext, needed only where a failed flush left an entity in a state the
    /// caller's own write must not inherit.
    /// </summary>
    private DbContext Context =>
        db as DbContext
        ?? throw new InvalidOperationException("The idea planner needs the EF Core context behind IAppDbContext.");

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
}
