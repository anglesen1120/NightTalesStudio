using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Text;
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
/// whole batch is stored or none of it is, and a caller that already opened a transaction still owns the commit.
/// The paid call meters itself, one <c>usage_records</c> row per call (<c>operation = idea</c>), because the
/// generator sees the call's outcome (D14).
/// </para>
/// <para>
/// <b>Claims and retries.</b> D7 allows one retry, and only for an output the schema or the validator refused:
/// <c>attempt = 2</c> is the second and last call of the run, so a run makes at most two paid calls in total.
/// Hangfire does not retry the caller's job (<c>Retries = 0</c>); a crash leaves the run to the sweeper.
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
        // when the bible is absent, so the same text is never paid for twice (task file [DECISION]).
        var recentEpisodes = new List<EpisodeSummaryRef>();
        if (context.Bible is null)
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

        // One SaveChanges is one transaction: the batch is stored whole or not at all. A caller that opened its
        // own transaction (02.8.3-T1 inserts the run row first) still owns the commit, and this write joins it.
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
            // 409 class as the refused transition one level up. The row is left `Modified` in the caller's
            // tracked context by the failed flush, and the caller's own SaveChanges (the run outcome) must not
            // replay it, so exactly this entry is detached — a ChangeTracker.Clear() would drop the caller's
            // pending work with it.
            Context.Entry(idea).State = EntityState.Detached;
            LogMarkUsedRefused(ideaId, IdeaErrors.EpisodeAlreadyUsed.Code);

            return IdeaErrors.EpisodeAlreadyUsed;
        }

        return Result.Success;
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
                    if (attempt == 1)
                    {
                        // D7's one retry. The second refusal is final: the run has no idea (the failure table's
                        // "zero valid candidates").
                        continue;
                    }

                    LogFailed(runId, DetailInvalidOutputTwice);

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
