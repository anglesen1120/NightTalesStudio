using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Structure;

// The feature namespace `…Features.SeriesBible` shadows the Domain aggregate's name inside this folder, and
// `NightTales.Domain.Series` shadows the series root's, so both are aliased — the shape BibleProposalApplier
// documents.
using SeriesAggregate = NightTales.Domain.Series.Series;
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Application.Features.SeriesBible.Jobs;

/// <summary>
/// The eight steps of one bible-proposal run (02.6.3-T2 plan §7.1): the paid call that turns one approved
/// episode into append-only bible ops, the post-processing that decides which of them may be stored, and the
/// branch that either stages them as the series' pending proposal (<c>ApprovalSource = user</c>) or applies
/// them immediately (<c>ApprovalSource = autopilot</c>, F27).
/// </summary>
/// <remarks>
/// <para>
/// The run is <b>series-level</b>: <c>generation_runs.episode_id</c> is NULL for <c>kind = bible_proposal</c>
/// (D2's scope rule), so the approved episode travels as a job argument and is re-checked against the run's
/// series here. One paid call, one usage row (the generator meters its own call, D14) and one settle or refund
/// per run (D1).
/// </para>
/// <para>
/// <b>Claims.</b> Only a <c>queued</c> run is claimable, under one conditional <c>UPDATE</c> that takes the row
/// lock: a second delivery of the same run — the outbox is at-least-once — finds it <c>running</c> or terminal
/// and stops, and <c>generation_runs.attempt</c> is the one retry the D7 invalid-output rule allows. A crash
/// leaves the run <c>running</c> and the <c>StaleGenerationSweeperJob</c> closes it; this job never retries
/// Hangfire-side (<c>Retries = 0</c>).
/// </para>
/// <para>
/// <b>Writes.</b> Every <c>series_bibles</c> write happens after
/// <see cref="IAppDbContext.LockSeriesBibleForUpdateAsync"/>, because the row's <c>version</c> is not an EF
/// concurrency token: that is the same order the manual accept command takes, so a proposal landing while the
/// owner saves the bible cannot interleave (plan §4 U4).
/// </para>
/// <para>
/// <b>Logs</b> carry ids, versions, counts and the validator's closed reason codes only — never bible text, the
/// story body, an op's text or a prompt (D19).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model of the delivery's scope, pinned to the run's owner.</param>
/// <param name="clock">The clock every stamp of this run is taken from.</param>
/// <param name="ledger">The credit ledger: the run's reservation is settled on success and refunded exactly once on failure.</param>
/// <param name="budgetGuard">The platform spend cap, re-checked before the paid call (D1: checked before every dispatch).</param>
/// <param name="generator">The one text call of the run; it meters its own usage row (D14).</param>
/// <param name="applier">The single write path of an autopilot auto-accept, shared with the manual accept command.</param>
/// <param name="proposalSwitch">The <c>Bible:Proposal:Enabled</c> switch, read per delivery (S7): off closes a queued run with no paid call.</param>
/// <param name="moderationEvents">The one writer of <c>moderation_events</c> (D12): a refusal on this run writes one row.</param>
/// <param name="audit">The audit writer: the autopilot auto-accept writes its row by hand (09.3.6 covers commands only).</param>
/// <param name="prices">The price catalog: the run's <c>queued_at</c> picks the version it is costed with.</param>
/// <param name="prompts">The prompt catalog; it composes the run's <c>prompt_version</c>.</param>
/// <param name="limits">The configured content limits the post-filter and the applier both enforce.</param>
/// <param name="caps">The op caps of <c>Bible:Proposal</c> (<c>BibleProposalJobOptions</c>, bound by the Worker).</param>
/// <param name="metrics">The feature's instruments: the ops this run dropped, by kind and reason.</param>
/// <param name="logger">The outcome log of this run.</param>
public sealed partial class ProposeBibleUpdateHandler(
    IAppDbContext db,
    IClock clock,
    ICreditLedger ledger,
    IBudgetGuard budgetGuard,
    IBibleProposalGenerator generator,
    IBibleProposalApplier applier,
    IBibleProposalSwitch proposalSwitch,
    IModerationEventWriter moderationEvents,
    IAuditWriter audit,
    IPriceCatalog prices,
    IPromptCatalog prompts,
    BibleLimits limits,
    BibleProposalCaps caps,
    SeriesBibleMetrics metrics,
    ILogger<ProposeBibleUpdateHandler> logger)
{
    /// <summary>The D13 action of a proposal the autopilot applied without the owner's accept.</summary>
    public const string AutoAcceptedAction = "series.bible_proposal_accepted";

    /// <summary>The detail written for each failure path; safe text, never content (D19).</summary>
    private const string DetailRunMissing = "run_missing";

    private const string DetailAlreadyProposed = "already_proposed";
    private const string DetailSpendCapReached = "spend_cap_reached";
    private const string DetailEpisodeNotApproved = "episode_not_approved";
    private const string DetailStoryUnreadable = "story_unreadable";
    private const string DetailBibleMissing = "bible_missing";
    private const string DetailPriceCatalogEmpty = "price_catalog_empty";
    private const string DetailReservationMissing = "reservation_missing";
    private const string DetailProviderRefused = "provider_refused";
    private const string DetailProviderUnavailable = "provider_unavailable";
    private const string DetailProviderError = "provider_error";
    private const string DetailFundsExhausted = "funds_exhausted";
    private const string DetailInvalidOutputTwice = "invalid_output_twice";
    private const string DetailInvalidOutputMaxTokens = "invalid_output_max_tokens";
    private const string DetailUnexpected = "unexpected_error";

    /// <summary>The closed scope-drift reasons a queued run is refused for (C4); the same text is the failure detail.</summary>
    private const string ScopeEpisodeNotNull = "episode_not_null";
    private const string ScopeSeriesMismatch = "series_mismatch";
    private const string ScopeEpisodeSeriesMismatch = "episode_series_mismatch";
    private const string ScopeTrueCrimeAutopilot = "true_crime_autopilot";
    private const string DetailFeatureDisabled = "feature_disabled";
    private const string DetailSeriesMissing = "series_missing";
    private const string DetailSeriesArchived = "series_archived";
    private const string DetailEpisodeMissing = "episode_missing";

    /// <summary>The ledger reason of each refund, the D1 vocabulary of a released reservation.</summary>
    private const string RefundInvalidOutput = "bible_proposal_invalid_output";

    private const string RefundModerated = "bible_proposal_moderated";
    private const string RefundFailed = "bible_proposal_generation_failed";
    private const string RefundUnavailable = "provider_unavailable";
    private const string RefundFundsExhausted = "provider_funds_exhausted";
    private const string RefundSpendCap = "spend_cap_reached";

    /// <summary>The ledger reason of a run the switch closed (S7): the feature was off when this delivery ran.</summary>
    private const string RefundFeatureDisabled = "feature_disabled";

    /// <summary>The Postgres <c>unique_violation</c> SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>The longest detail string written to <c>generation_runs.models</c>.</summary>
    private const int DetailMaxChars = 64;

    /// <summary>The write budget of the terminal write, which must survive a shutdown (class 3).</summary>
    private static readonly TimeSpan TerminalWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The ledger call budget, mirroring <c>GenerateFactSheetPipeline</c>.</summary>
    private static readonly TimeSpan LedgerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The prompt files the one call uses; the generator reads the same keys.</summary>
    private static readonly PromptVersionParts PromptParts =
        new(BibleProposalPrompts.SystemKey, BibleProposalPrompts.SystemKey, BibleProposalPrompts.SchemaKey);

    /// <summary>
    /// Runs the bible proposal of <paramref name="generationRunId"/> (02.6.3-T2 plan §7.1).
    /// </summary>
    /// <param name="seriesId">The series the run belongs to; the run row carries it too, and a disagreement is refused.</param>
    /// <param name="episodeId">The approved episode the proposal was bought for.</param>
    /// <param name="generationRunId">The run.</param>
    /// <param name="jobToken">Hangfire's shutdown token: cancelling it leaves the run to the sweeper.</param>
    /// <returns>A task that completes when the run reached a status, or was left to another actor.</returns>
    public async Task RunAsync(Guid seriesId, Guid episodeId, Guid generationRunId, CancellationToken jobToken)
    {
        var state = await LoadAsync(seriesId, episodeId, generationRunId, jobToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        try
        {
            await ExecuteAsync(state, jobToken).ConfigureAwait(false);
        }
        catch (Exception) when (jobToken.IsCancellationRequested)
        {
            // Class 3: the host is shutting down. Whatever the step threw, the run stays open and the sweeper
            // reclaims it, so no write is attempted — the shutdown owns the outcome, not the failure.
            LogCancelled(generationRunId);
        }
        catch (Exception exception)
        {
            DiscardPendingChanges();
            LogUnexpected(generationRunId, exception.GetType().Name);
            await FailAsync(state, ErrorCodes.BibleProposalGenerationFailed, DetailUnexpected, RefundFailed).ConfigureAwait(false);
        }
    }

    // Steps 1 to 8, in order; every step that returns false has already written the run's outcome.
    private async Task ExecuteAsync(State s, CancellationToken ct)
    {
        if (!await ClaimAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await PlanAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        var draft = await ProposeAsync(s, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return;
        }

        PostProcess(s, draft);

        await SaveAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the run and holds every case in which this delivery must do nothing, before the row is claimed.
    /// </summary>
    /// <remarks>
    /// A terminal run is a replay of a delivery that already finished, and a <c>running</c> run belongs to the
    /// worker that claimed it (a crashed one is the sweeper's, not this delivery's). A queued run that this delivery
    /// cannot run (the switch is off, the series is gone or archived, or the run's scope has drifted) is closed here
    /// as <c>failed</c> with its reservation refunded and no paid call, so it never sits queued with its reservation
    /// open (02.6.3-T2 C4, S7). Such a run never counts toward the limit (<c>counts_toward_limit = false</c>).
    /// </remarks>
    private async Task<State?> LoadAsync(Guid seriesId, Guid episodeId, Guid runId, CancellationToken ct)
    {
        var run = await db.GenerationRuns
            .SingleOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null)
        {
            LogRunMissing(runId);
            return null;
        }

        if (run.Kind != GenerationKind.BibleProposal)
        {
            LogRunNotBibleProposal(runId, run.Kind.ToString());
            return null;
        }

        if (run.Status is GenerationRunStatus.Succeeded or GenerationRunStatus.Failed or GenerationRunStatus.Cancelled)
        {
            LogRunTerminal(runId, run.Status);
            return null;
        }

        if (run.Status == GenerationRunStatus.Running)
        {
            LogRunHeld(runId);
            return null;
        }

        if (!proposalSwitch.Enabled)
        {
            // S7: the switch is read on every delivery, so a run queued before it went off closes here, with no paid call.
            await FailRunAsync(run, run.SeriesId, ErrorCodes.FeatureDisabled, DetailFeatureDisabled, RefundFeatureDisabled)
                .ConfigureAwait(false);
            return null;
        }

        if (run.EpisodeId is not null)
        {
            // `bible_proposal` is a series-level kind: a row that names an episode is drift, not work (D2/T17).
            LogRunScopeInvalid(runId, ScopeEpisodeNotNull);
            await FailRunAsync(run, run.SeriesId, ErrorCodes.BibleProposalGenerationFailed, ScopeEpisodeNotNull, RefundFailed)
                .ConfigureAwait(false);
            return null;
        }

        if (run.SeriesId != seriesId)
        {
            LogRunScopeInvalid(runId, ScopeSeriesMismatch);
            await FailRunAsync(run, run.SeriesId, ErrorCodes.BibleProposalGenerationFailed, ScopeSeriesMismatch, RefundFailed)
                .ConfigureAwait(false);
            return null;
        }

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == run.SeriesId, ct)
            .ConfigureAwait(false);
        if (series is null)
        {
            LogSeriesMissing(runId, run.SeriesId);
            await FailRunAsync(run, run.SeriesId, ErrorCodes.NotFound, DetailSeriesMissing, RefundFailed).ConfigureAwait(false);
            return null;
        }

        if (series.ArchivedAt is not null)
        {
            LogSeriesArchived(runId, series.Id);
            await FailRunAsync(run, series.Id, ErrorCodes.SeriesArchived, DetailSeriesArchived, RefundFailed).ConfigureAwait(false);
            return null;
        }

        // The one case the plan refuses rather than skips: true crime is never auto-approved (02.8.1), so a run
        // that says it was is drift; the guard is defensive and mirrors the handler's.
        if (run.Trigger == GenerationTrigger.Autopilot && series.Topic == NightTales.Domain.Series.Topic.TrueCrime)
        {
            LogTrueCrimeAutopilot(runId, series.Id);
            await FailRunAsync(run, series.Id, ErrorCodes.BibleProposalGenerationFailed, ScopeTrueCrimeAutopilot, RefundFailed)
                .ConfigureAwait(false);
            return null;
        }

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        if (episode is null)
        {
            LogEpisodeMissing(runId, episodeId);
            await FailRunAsync(run, series.Id, ErrorCodes.NotFound, DetailEpisodeMissing, RefundFailed).ConfigureAwait(false);
            return null;
        }

        if (episode.SeriesId != series.Id)
        {
            LogRunScopeInvalid(runId, ScopeEpisodeSeriesMismatch);
            await FailRunAsync(run, series.Id, ErrorCodes.BibleProposalGenerationFailed, ScopeEpisodeSeriesMismatch, RefundFailed)
                .ConfigureAwait(false);
            return null;
        }

        var bible = await db.SeriesBibles
            .SingleOrDefaultAsync(candidate => candidate.SeriesId == series.Id, ct)
            .ConfigureAwait(false);
        if (bible is null)
        {
            // Data drift: the create hook and the backfill own the row (02.6.3-T1). Nothing can be proposed onto
            // a bible that does not exist, and the alert is the metric the read path raises too.
            metrics.Missing();
            LogBibleMissing(runId, series.Id);
            await FailAsync(
                new State(run, series, episode, SeriesBibleEntity.CreateDefault(series.Id, series.UserId, clock)),
                ErrorCodes.BibleProposalGenerationFailed,
                DetailBibleMissing,
                RefundFailed).ConfigureAwait(false);
            return null;
        }

        return new State(run, series, episode, bible);
    }

    // Step 1: the conditional claim, `start`, the `writing` stage and the prompt version, in one transaction.
    private async Task<bool> ClaimAsync(State s, CancellationToken ct)
    {
        var now = clock.UtcNow;

        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        // Only a queued run is claimable, and the whole decision is one UPDATE under the row lock: two deliveries
        // of the same run cannot both pass it, and the committed state fails the loser's own reload below.
        var claimed = await db.GenerationRuns
            .Where(candidate => candidate.Id == s.Run.Id && candidate.Status == GenerationRunStatus.Queued)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.UpdatedAt, now)
                    .SetProperty(candidate => candidate.StartedAt, now),
                ct)
            .ConfigureAwait(false);
        if (claimed == 0)
        {
            LogStopped(s.Run.Id, "claim_rejected");
            return false;
        }

        var run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
        if (run is null)
        {
            LogStopped(s.Run.Id, DetailRunMissing);
            return false;
        }

        run.Start(now);

        if (run.Stage < GenerationStage.Writing)
        {
            var advanced = run.AdvanceStage(GenerationStage.Writing, now);
            if (advanced.IsError)
            {
                LogStopped(s.Run.Id, advanced.FirstError.Code);
                return false;
            }
        }

        run.RecordProvenance(prompts.BuildPromptVersion(PromptParts), Provenance(s.Model, validation: null, modelOps: 0), now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await write.CommitAsync(ct).ConfigureAwait(false);

        s.Adopt(run);

        return true;
    }

    /// <summary>
    /// Whether this delivery repeats a run that already succeeded (C7): another bible-proposal run of the same series,
    /// created by the same idempotency key, has the <c>succeeded</c> status. A run whose key is null is never a replay.
    /// </summary>
    /// <param name="s">The run's state.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>True when an earlier run with the same key already succeeded.</returns>
    private async Task<bool> ExactReplayAsync(State s, CancellationToken ct)
    {
        if (s.Run.IdempotencyKey is not { } key)
        {
            return false;
        }

        return await db.GenerationRuns
            .AnyAsync(
                candidate => candidate.SeriesId == s.Series.Id
                    && candidate.Kind == GenerationKind.BibleProposal
                    && candidate.IdempotencyKey == key
                    && candidate.Status == GenerationRunStatus.Succeeded
                    && candidate.Id != s.Run.Id,
                ct)
            .ConfigureAwait(false);
    }

    // Steps 2 and 3: the guards the run re-checks for itself, then the inputs of the prompt.
    private async Task<bool> PlanAsync(State s, CancellationToken ct)
    {
        // The replay guard (plan §7.1 step 2, C7): a delivery is a replay only when another run of this series with the
        // same idempotency key already succeeded. The key is per (episode, story version), so a new story version of
        // an episode that already has a pending proposal is a new run and is not skipped.
        if (await ExactReplayAsync(s, ct).ConfigureAwait(false))
        {
            LogStopped(s.Run.Id, DetailAlreadyProposed);
            await SettleAsync(s, ct).ConfigureAwait(false);
            return false;
        }

        // The paid dispatch re-checks the platform cap, with this run's own bound left out of the open sum: the
        // admission already reserved it (D1). A nice-to-have must never break the budget (plan §7.1 step 3).
        var allowed = await budgetGuard
            .CheckAsync(new CostBound(s.Run.WorstCaseUsd), ct, s.Run.Id)
            .ConfigureAwait(false);
        if (allowed.IsError)
        {
            await FailAsync(s, ErrorCodes.SpendCapReached, DetailSpendCapReached, RefundSpendCap).ConfigureAwait(false);
            return false;
        }

        // The episode must still be approved: a reopened story (02.3.2) removed the version this proposal is
        // built from, so there is no prompt to build.
        if (s.Episode.ApprovedStoryVersionId is not { } versionId)
        {
            await FailAsync(s, ErrorCodes.BibleProposalGenerationFailed, DetailEpisodeNotApproved, RefundFailed).ConfigureAwait(false);
            return false;
        }

        var version = await db.StoryVersions
            .AsNoTracking()
            .Where(candidate => candidate.Id == versionId)
            .Select(candidate => new { candidate.Content, candidate.CreatedAt })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (version is null || !StoryContentJson.TryReadStored(version.Content, out var story))
        {
            await FailAsync(s, ErrorCodes.BibleProposalGenerationFailed, DetailStoryUnreadable, RefundFailed).ConfigureAwait(false);
            return false;
        }

        var price = await prices.GetActiveAsync(s.Run.QueuedAt, ct).ConfigureAwait(false);
        if (price.IsError)
        {
            await FailAsync(s, ErrorCodes.BibleProposalGenerationFailed, DetailPriceCatalogEmpty, RefundFailed).ConfigureAwait(false);
            return false;
        }

        if (s.Run.ReservationId is not { } reservation)
        {
            await FailAsync(s, ErrorCodes.BibleProposalGenerationFailed, DetailReservationMissing, RefundFailed).ConfigureAwait(false);
            return false;
        }

        s.PriceVersion = price.Value.Version.ToString(CultureInfo.InvariantCulture);
        s.Context = new RunContext(
            s.Run.Id,
            s.Run.UserId,
            s.Run.SeriesId,
            s.Episode.Id,
            new ReservationId(reservation),
            s.Run.Trigger,
            // The D7 retry rewrites the attempt on a copy of this context; the scope always carries the first.
            Attempt: 1,
            s.PriceVersion);
        s.Structure = StoryStructureTags.FromStoryContent(story);
        s.StoryTitle = story.Title;
        s.StoryBody = DerivedStoryFields.From(story).Body;

        // The two sources of the deterministic summary fallback (plan §7.1 step 6), read here so the
        // post-process needs no second read of the version.
        s.StoryDescription = story.Description;
        s.StoryHook = story.Hook.Text;
        s.SummarySource = new BibleSummarySource(
            s.Episode.Id,
            s.Structure,
            s.Episode.ApprovedAt ?? version.CreatedAt);

        return true;
    }

    // Step 4 (the `writing` stage): one paid call, with the single D7 invalid-output retry.
    private async Task<BibleProposalV1?> ProposeAsync(State s, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var request = new BibleProposalRequest(
                s.Context! with { Attempt = attempt },
                SnakeCase(s.Series.Language.ToString()),
                SnakeCase(s.Series.Topic.ToString()),
                s.Bible.Content,
                s.Structure!,
                s.StoryTitle!,
                s.StoryBody!);

            var result = await generator.GenerateAsync(request, ct).ConfigureAwait(false);

            switch (result)
            {
                case TextCallOk<BibleProposalV1> ok:
                    s.Model = TextSafety.CleanModelName(ok.Model);
                    return ok.Draft;

                case TextCallInvalidOutput<BibleProposalV1> invalid:
                    LogInvalidOutput(s.Run.Id, attempt);
                    // P2: a stop at max_output_tokens would repeat the same truncation, so it fails on the first call.
                    if (attempt == 1 && !invalid.RuleIds.Contains(TextCallInvalidOutput<BibleProposalV1>.MaxOutputTokensExceeded))
                    {
                        if (!await RecordAttemptAsync(s, ct).ConfigureAwait(false))
                        {
                            return null;
                        }

                        continue;
                    }

                    await FailAsync(
                        s,
                        ErrorCodes.BibleProposalInvalidOutput,
                        invalid.RuleIds.Contains(TextCallInvalidOutput<BibleProposalV1>.MaxOutputTokensExceeded)
                            ? DetailInvalidOutputMaxTokens
                            : DetailInvalidOutputTwice,
                        RefundInvalidOutput).ConfigureAwait(false);
                    return null;

                case TextCallModerated<BibleProposalV1> moderated:
                    // TL ruling #3 (2026-09-24): a refusal on a series-level run writes moderation_events with
                    // `stage = kind`, so the row names the stage this run is (plan §7.1 step 4).
                    await WriteModerationEventAsync(s, moderated.Categories).ConfigureAwait(false);
                    await FailAsync(s, ErrorCodes.BibleProposalModerated, DetailProviderRefused, RefundModerated).ConfigureAwait(false);
                    return null;

                case TextCallFundsExhausted<BibleProposalV1>:
                    await FailAsync(s, ErrorCodes.ProviderFundsExhausted, DetailFundsExhausted, RefundFundsExhausted).ConfigureAwait(false);
                    return null;

                case TextCallUnavailable<BibleProposalV1>:
                    await FailAsync(s, ErrorCodes.BibleProposalGenerationFailed, DetailProviderUnavailable, RefundUnavailable).ConfigureAwait(false);
                    return null;

                default:
                    await FailAsync(s, ErrorCodes.BibleProposalGenerationFailed, DetailProviderError, RefundFailed).ConfigureAwait(false);
                    return null;
            }
        }
    }

    // Steps 5 and 6: the model's ops are untrusted, so the validator decides which may be stored, and an
    // approved episode always ends up with exactly one summary (F27).
    private void PostProcess(State s, BibleProposalV1 draft)
    {
        var candidates = draft.ToCandidates();
        s.ModelOps = candidates.Count;

        var validation = BibleProposalValidator.Validate(candidates, s.Bible.Content, caps, limits);
        s.Drops = validation.Drops;

        foreach (var group in validation.Drops
            .GroupBy(drop => (drop.Kind, drop.Reason))
            .OrderBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Reason, StringComparer.Ordinal))
        {
            metrics.OpsDropped(group.Key.Kind, group.Key.Reason, group.Count());
        }

        // Every summary op this run builds summarises the approved episode, so it is stamped with that episode (C1):
        // a merge of two episodes then keeps each summary on its own episode.
        var ops = validation.Ops
            .Select(op => op.Kind == BibleOpKind.AddSummary ? op with { EpisodeId = s.Episode.Id } : op)
            .ToList();
        if (!ops.Any(op => op.Kind == BibleOpKind.AddSummary))
        {
            var fallback = SummaryFallback(s);
            if (fallback is not null)
            {
                ops.Add(fallback);
                s.SummaryFallback = true;
            }
            else
            {
                LogSummaryFallbackUnavailable(s.Run.Id, s.Episode.Id);
            }
        }

        s.Ops = ops;
    }

    /// <summary>
    /// The summary op of an approval that produced none (plan §7.1 step 6): the story's own description cut to
    /// the summary limit on a word boundary, or its hook when the description is empty. Every approval therefore
    /// leaves one summary behind, which is what makes the bible a record of the series.
    /// </summary>
    /// <param name="s">The run's state, with the approved story already read.</param>
    /// <returns>The op, or <see langword="null"/> when the story carries neither a description nor a hook.</returns>
    private BibleOp? SummaryFallback(State s)
    {
        var text = BibleOpNormalizer.Truncate(s.StoryDescription, limits.SummaryChars);
        if (text.Length == 0)
        {
            text = BibleOpNormalizer.Truncate(s.StoryHook, limits.SummaryChars);
        }

        if (text.Length == 0)
        {
            return null;
        }

        // The fallback is a summary like any model op, so it is stamped with the episode and checked the same way the
        // accept path checks it (S4): a story text with a refused character leaves the approval without a summary,
        // which is logged, instead of storing an op the accept would drop.
        var fallback = new BibleOp(
            BibleOpKind.AddSummary,
            text,
            BibleOpNormalizer.Truncate(s.StoryTitle, limits.SummaryTitleChars),
            Description: null,
            Text: null,
            Reason: null,
            EpisodeId: s.Episode.Id);

        return BibleProposalValidator.IsStorable(fallback) ? fallback : null;
    }

    // Steps 7 and 8 (the `saving` stage): one transaction for the proposal, the autopilot audit row and the run
    // outcome, then the ledger settle that never runs inside it.
    private async Task SaveAsync(State s, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var autopilot = s.Run.Trigger == GenerationTrigger.Autopilot;
        var applied = 0;
        var skipped = 0;
        var fromVersion = 0;
        var toVersion = 0;
        var staged = false;
        GenerationRun? run = null;

        await using (var write = await BeginAsync(ct).ConfigureAwait(false))
        {
            // Every writer of `series_bibles` locks the row before it loads it (02.6.3-T1 rule): `version` is not
            // an EF concurrency token, so the lock is what serialises this write against a manual save.
            await db.LockSeriesBibleForUpdateAsync(s.Series.Id, ct).ConfigureAwait(false);

            run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
            var bible = await FreshAsync(db.SeriesBibles, s.Bible.Id, ct).ConfigureAwait(false);
            if (run is null || bible is null)
            {
                LogStopped(s.Run.Id, DetailBibleMissing);
                return;
            }

            var cost = await CostAsync(run, ct).ConfigureAwait(false);
            fromVersion = bible.Version;

            if (autopilot)
            {
                var result = ApplyAutopilot(s, bible, now);
                applied = result.Applied.Count;
                skipped = result.Skipped;

                // C6: an apply that moved no version (no ops, or every op skipped) has no accepted proposal to audit.
                if (bible.Version > fromVersion)
                {
                    await audit.WriteAsync(
                        new AuditEntry(
                            ActorUserId: run.UserId,
                            // The job runs under SystemContext: the row says the platform applied the ops, on the
                            // owner's behalf (the ApproveStoryCommandHandler shape for a system-context write).
                            ActorType: ActorType.System,
                            SubjectUserId: run.UserId,
                            Action: AutoAcceptedAction,
                            Category: Category.Episodes,
                            Visibility: Visibility.User,
                            EntityType: "series",
                            EntityId: s.Series.Id,
                            DataJson: AuditData(s, bible, applied, skipped, fromVersion),
                            Ip: null,
                            UserAgent: null,
                            ActorLabel: null,
                            OccurredAt: now),
                        ct).ConfigureAwait(false);
                }
            }
            else
            {
                // The pending proposal's legacy summaries are bound to the episode it was made from first, so the merge
                // keeps each summary on its own episode (C1).
                var existing = StampLegacySummaries(bible.ProposedContent, bible.ProposedFromEpisodeId);
                var merged = BibleProposalValidator.Merge(existing, s.Ops, caps.MaxOps);
                if (merged.Count == 0)
                {
                    // Nothing survived post-processing and the series has no pending proposal: the run is a
                    // success with no content change (it is still one paid call, so it settles).
                    LogNoOps(s.Run.Id, s.Episode.Id);
                }
                else
                {
                    // A merge keeps the pending proposal's BasedOnVersion and ProposedAt (C5, plan §7.1): the ops were
                    // computed against that version, and a version bump between two approvals does not re-stamp them.
                    // Only the episode the proposal is pending for moves to this run's episode.
                    var basedOn = existing?.BasedOnVersion ?? bible.Version;
                    var proposedAt = existing?.ProposedAt ?? now;
                    bible.SetProposal(new BibleProposal(merged, basedOn, proposedAt), s.Episode.Id, clock);
                    staged = true;
                }
            }

            toVersion = bible.Version;

            // The provenance the run row keeps (D2): the composite prompt version again, now with the model the
            // call used and the safe op counts of this run — the precedent re-records it once the call is done
            // (GenerateFactSheetPipeline). Never content, only counts (D19).
            run.RecordProvenance(
                prompts.BuildPromptVersion(PromptParts),
                Provenance(s.Model, new BibleProposalValidationResult(s.Ops, s.Drops), s.ModelOps),
                now);

            run.Succeed(cost, null, null, now);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        if (run is null)
        {
            return;
        }

        LogSucceeded(
            run.Id,
            s.Series.Id,
            s.Episode.Id,
            fromVersion,
            toVersion,
            staged ? "staged" : autopilot ? "applied" : "empty",
            s.ModelOps,
            s.Ops.Count,
            s.Drops.Count,
            applied,
            skipped);

        await SettleRunAsync(run).ConfigureAwait(false);
    }

    /// <summary>
    /// The summary ops of a pending proposal that predates the episode stamp (C1) bound to the episode that proposal
    /// was made from, so a merge keeps them on that episode. Nothing else changes; a proposal with no unstamped summary
    /// is returned as it is.
    /// </summary>
    /// <param name="proposal">The pending proposal, when the series has one.</param>
    /// <param name="proposedFromEpisodeId">The episode the pending proposal was made from.</param>
    /// <returns>The proposal with its legacy summaries stamped, or the proposal itself.</returns>
    private static BibleProposal? StampLegacySummaries(BibleProposal? proposal, Guid? proposedFromEpisodeId)
    {
        if (proposal is null || proposedFromEpisodeId is not { } episodeId)
        {
            return proposal;
        }

        if (!proposal.Ops.Any(op => op.Kind == BibleOpKind.AddSummary && op.EpisodeId is null))
        {
            return proposal;
        }

        return proposal with
        {
            Ops = proposal.Ops
                .Select(op => op.Kind == BibleOpKind.AddSummary && op.EpisodeId is null ? op with { EpisodeId = episodeId } : op)
                .ToList(),
        };
    }

    /// <summary>
    /// Applies this run's ops immediately, the autopilot way (plan §7.1 step 7, F27): the ops are staged on the
    /// row the applier reads, applied onto the current content, and a proposal that was already pending — another
    /// episode's, waiting for the owner — is put back untouched.
    /// </summary>
    /// <remarks>
    /// This run's ops all summarise the approved episode, so its summary source is the only one passed: the applier
    /// binds each summary to its own episode id. A merged pending proposal of another episode is left untouched
    /// here; it belongs to the accept or dismiss it waits for.
    /// </remarks>
    /// <param name="s">The run's state.</param>
    /// <param name="bible">The tracked bible row, freshly read under its lock.</param>
    /// <param name="now">The clock reading of this write.</param>
    /// <returns>The applier's result: the ops that changed the content and how many were skipped.</returns>
    private BibleApplyResult ApplyAutopilot(State s, SeriesBibleEntity bible, DateTimeOffset now)
    {
        if (s.Ops.Count == 0)
        {
            return new BibleApplyResult([], 0);
        }

        var pending = bible.ProposedContent;
        var pendingFrom = bible.ProposedFromEpisodeId;

        if (s.SummarySource is not { } source)
        {
            throw new InvalidOperationException("The autopilot needs the run's summary source: PlanAsync sets it before any write.");
        }

        bible.SetProposal(new BibleProposal(s.Ops, bible.Version, now), s.Episode.Id, clock);
        var applied = applier.Apply(s.Series, bible, [source], s.Run.UserId);

        if (pending is not null && pendingFrom is { } from)
        {
            bible.SetProposal(pending, from, clock);
        }

        return applied;
    }

    /// <summary>Runs the guard-free success path of a run that had nothing to write (the merge guard, step 2).</summary>
    /// <param name="s">The run's state.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task that completes when the run is <c>succeeded</c> and its reservation is settled.</returns>
    private async Task SettleAsync(State s, CancellationToken ct)
    {
        GenerationRun? run = null;

        await using (var write = await BeginAsync(ct).ConfigureAwait(false))
        {
            run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
            if (run is null || run.Status != GenerationRunStatus.Running)
            {
                LogStopped(s.Run.Id, DetailRunMissing);
                return;
            }

            run.Succeed(await CostAsync(run, ct).ConfigureAwait(false), null, null, clock.UtcNow);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await write.CommitAsync(ct).ConfigureAwait(false);
        }

        if (run is null)
        {
            return;
        }

        LogSucceeded(run.Id, s.Series.Id, s.Episode.Id, s.Bible.Version, s.Bible.Version, "already_proposed", 0, 0, 0, 0, 0);
        await SettleRunAsync(run).ConfigureAwait(false);
    }

    /// <summary>Ends the run as failed and releases its reservation, both exactly once (D1).</summary>
    /// <param name="s">The run's state.</param>
    /// <param name="code">The D8 outcome code.</param>
    /// <param name="detail">Safe text about the outcome, never content (D19).</param>
    /// <param name="refundReason">The D1 refund reason.</param>
    /// <returns>A task that completes when the outcome is committed and the reservation released.</returns>
    private Task FailAsync(State s, string code, string detail, string refundReason) =>
        FailRunAsync(s.Run, s.Series.Id, code, detail, refundReason);

    /// <summary>
    /// Ends a run as failed and releases its reservation, both exactly once (D1). Used by the states that hold a
    /// <see cref="State"/> and by the LoadAsync skips, which hold only the run row (C4).
    /// </summary>
    /// <param name="loaded">The run row as the delivery loaded it; the terminal write reads it afresh.</param>
    /// <param name="seriesId">The series the run belongs to, for the outcome log.</param>
    /// <param name="code">The D8 outcome code.</param>
    /// <param name="detail">Safe text about the outcome, never content (D19).</param>
    /// <param name="refundReason">The D1 refund reason.</param>
    /// <returns>A task that completes when the outcome is committed and the reservation released.</returns>
    private async Task FailRunAsync(GenerationRun loaded, Guid seriesId, string code, string detail, string refundReason)
    {
        // A fresh token: the job token is not the terminal write's to cancel (class 3), and the deadline the
        // shutdown leaves behind must not turn a typed failure into an open run.
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        var written = false;

        try
        {
            await using (var write = await BeginAsync(terminal.Token).ConfigureAwait(false))
            {
                var run = await FreshAsync(db.GenerationRuns, loaded.Id, terminal.Token).ConfigureAwait(false);
                if (run is null
                    || run.Status is GenerationRunStatus.Succeeded or GenerationRunStatus.Failed or GenerationRunStatus.Cancelled)
                {
                    // Another actor already ended it: the run is never overwritten, and the refund is its own call's.
                    LogStopped(loaded.Id, "already_ended");
                    return;
                }

                // Every bible proposal run is a nice-to-have: it never counts toward FR-02-7 (D2 counts kind = story).
                run.Fail(code, detail, countsTowardLimit: false, clock.UtcNow);

                await db.SaveChangesAsync(terminal.Token).ConfigureAwait(false);
                await write.CommitAsync(terminal.Token).ConfigureAwait(false);
                written = true;
            }
        }
        catch (Exception exception)
        {
            // Type only: a provider or database message can echo content (D19).
            LogStopped(loaded.Id, exception.GetType().Name);
        }

        if (!written)
        {
            // The outcome was not recorded, so the run is still open: the sweeper owns it and its refund.
            return;
        }

        LogRunFailed(loaded.Id, seriesId, code, detail);
        await RefundAsync(loaded, refundReason).ConfigureAwait(false);
    }

    /// <summary>Counts one invalid-output retry (<c>attempt = 2</c>), the one retry D7 allows this run.</summary>
    /// <param name="s">The run's state.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns><see langword="true"/> when the attempt was recorded.</returns>
    private async Task<bool> RecordAttemptAsync(State s, CancellationToken ct)
    {
        var run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
        if (run is null)
        {
            LogStopped(s.Run.Id, DetailRunMissing);
            return false;
        }

        run.RecordAttempt(clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    private async Task WriteModerationEventAsync(State s, IReadOnlyCollection<string> categories)
    {
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        await moderationEvents
            .WriteAsync(
                s.Run.UserId,
                s.Episode.Id,
                ModerationSource.OpenaiText,
                ModerationStage.BibleProposal,
                categories,
                ownText: null,
                terminal.Token)
            .ConfigureAwait(false);
    }

    // The settled cost of the run: what the meter recorded for its reservation, in its own transaction (D14).
    private async Task<decimal> CostAsync(GenerationRun run, CancellationToken ct) =>
        run.ReservationId is { } reservation
            ? await db.UsageRecords
                .Where(record => record.ReservationId == reservation)
                .SumAsync(record => record.CostUsd, ct)
                .ConfigureAwait(false)
            : 0m;

    // Runs on its own 10 s budget, not on the job's token, so a terminal write never skips it.
    private async Task SettleRunAsync(GenerationRun run)
    {
        if (run.ReservationId is not { } reservation)
        {
            return;
        }

        using var budget = new CancellationTokenSource(LedgerTimeout);

        try
        {
            var settled = await ledger.SettleAsync(new ReservationId(reservation), budget.Token).ConfigureAwait(false);
            if (settled.IsError)
            {
                LogLedgerFailed(run.Id, "settle", settled.FirstError.Code);
            }
        }
        catch (Exception exception)
        {
            LogLedgerFailed(run.Id, "settle", exception.GetType().Name);
        }
    }

    private async Task RefundAsync(GenerationRun run, string reason)
    {
        if (run.ReservationId is not { } reservation)
        {
            return;
        }

        using var budget = new CancellationTokenSource(LedgerTimeout);

        try
        {
            var refunded = await ledger.RefundAsync(new ReservationId(reservation), reason, budget.Token).ConfigureAwait(false);
            if (refunded.IsError)
            {
                LogLedgerFailed(run.Id, "refund", refunded.FirstError.Code);
            }
        }
        catch (Exception exception)
        {
            LogLedgerFailed(run.Id, "refund", exception.GetType().Name);
        }
    }

    /// <summary>
    /// The autopilot audit row's <c>data</c> (plan §7.1 step 7, D13): ids, versions and counts, never an op's
    /// text or the bible's content (D19).
    /// </summary>
    /// <param name="s">The run's state.</param>
    /// <param name="bible">The bible after the apply.</param>
    /// <param name="applied">How many ops changed the content.</param>
    /// <param name="skipped">How many ops added nothing.</param>
    /// <param name="fromVersion">The content version before the apply.</param>
    /// <returns>The payload as JSON text.</returns>
    private static string AuditData(State s, SeriesBibleEntity bible, int applied, int skipped, int fromVersion) =>
        new JsonObject
        {
            ["seriesId"] = s.Series.Id.ToString("D", CultureInfo.InvariantCulture),
            ["fromVersion"] = fromVersion,
            ["toVersion"] = bible.Version,
            ["applied"] = applied,
            ["skipped"] = skipped,
            ["proposedFromEpisodeId"] = s.Episode.Id.ToString("D", CultureInfo.InvariantCulture),
        }.ToJsonString();

    // The run's provenance: the prompt version plus the safe run facts, never content (D19).
    private string Provenance(string? model, BibleProposalValidationResult? validation, int modelOps)
    {
        var bible = new JsonObject
        {
            ["model"] = model ?? string.Empty,
            ["caps"] = new JsonObject
            {
                ["max_ops"] = caps.MaxOps,
                ["max_character_ops"] = caps.MaxCharacterOps,
                ["max_place_ops"] = caps.MaxPlaceOps,
                ["max_rule_ops"] = caps.MaxRuleOps,
            },
        };

        if (validation is not null)
        {
            bible["model_ops"] = modelOps;
            bible["kept_ops"] = validation.Ops.Count;
            bible["dropped_ops"] = validation.Drops.Count;

            var drops = new JsonObject();
            foreach (var group in validation.Drops
                .GroupBy(drop => drop.Reason, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                drops[Bounded(group.Key)] = group.Count();
            }

            bible["drops"] = drops;
        }

        return new JsonObject { ["bible_proposal"] = bible }.ToJsonString();
    }

    // The write transaction of one call; disposing it without a commit rolls every pending write back.
    private async Task<WriteScope> BeginAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return new WriteScope(null);
        }

        var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        return new WriteScope(transaction);
    }

    // The entity as committed: a tracked instance is re-read (identity resolution would return its old values).
    private async Task<TEntity?> FreshAsync<TEntity>(DbSet<TEntity> set, Guid id, CancellationToken ct)
        where TEntity : Entity
    {
        var context = Context;
        var tracked = context.ChangeTracker.Entries<TEntity>().FirstOrDefault(entry => entry.Entity.Id == id);
        if (tracked is null)
        {
            return await set.SingleOrDefaultAsync(candidate => candidate.Id == id, ct).ConfigureAwait(false);
        }

        await tracked.ReloadAsync(ct).ConfigureAwait(false);

        return tracked.State == EntityState.Detached ? null : tracked.Entity;
    }

    private void DiscardPendingChanges()
    {
        if (db is DbContext context)
        {
            context.ChangeTracker.Clear();
        }
    }

    private DbContext Context =>
        db as DbContext ?? throw new InvalidOperationException("The bible proposal job needs the EF Core context behind IAppDbContext.");

    // A safe, bounded provenance string: C0 controls and lone surrogates out, then cut (a jsonb value refuses a NUL).
    private static string Bounded(string? value)
    {
        var cleaned = TextSafety.Clean(value);
        return cleaned.Length <= DetailMaxChars ? cleaned : cleaned[..DetailMaxChars];
    }

    /// <summary>The snake_case wire spelling of an enum member, the way the D6 data object names its control keys.</summary>
    /// <param name="member">The member name, for example <c>TrueCrime</c>.</param>
    /// <returns>The wire spelling, for example <c>true_crime</c>.</returns>
    private static string SnakeCase(string member) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(member);

    /// <summary>What one run's execution reads and accumulates, so no step re-reads the aggregates.</summary>
    private sealed class State(GenerationRun run, SeriesAggregate series, Episode episode, SeriesBibleEntity bible)
    {
        public GenerationRun Run { get; private set; } = run;

        public SeriesAggregate Series { get; } = series;

        public Episode Episode { get; } = episode;

        public SeriesBibleEntity Bible { get; } = bible;

        public string PriceVersion { get; set; } = string.Empty;

        public RunContext? Context { get; set; }

        public StoryStructureTags? Structure { get; set; }

        public string? StoryTitle { get; set; }

        public string? StoryBody { get; set; }

        public BibleSummarySource? SummarySource { get; set; }

        /// <summary>The op list this run staged or applied, after post-processing.</summary>
        public List<BibleOp> Ops { get; set; } = [];

        /// <summary>The candidates the validator refused, one per dropped op.</summary>
        public IReadOnlyList<BibleOpDrop> Drops { get; set; } = [];

        /// <summary>How many ops the model returned, before any rule.</summary>
        public int ModelOps { get; set; }

        /// <summary>Whether the summary op was the deterministic fallback of step 6.</summary>
        public bool SummaryFallback { get; set; }

        public string? Model { get; set; }

        /// <summary>The description of the approved story, for the summary fallback.</summary>
        public string? StoryDescription { get; set; }

        /// <summary>The hook text of the approved story, the fallback's second source.</summary>
        public string? StoryHook { get; set; }

        /// <summary>Takes the claimed instance the write reloaded, so later steps read the committed values.</summary>
        /// <param name="claimed">The run as committed by the claim.</param>
        public void Adopt(GenerationRun claimed) => Run = claimed;
    }

    // The write transaction of one call; disposing it without a commit rolls every pending write back.
    private sealed class WriteScope(IDbContextTransaction? transaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken ct) => transaction is null ? Task.CompletedTask : transaction.CommitAsync(ct);

        public ValueTask DisposeAsync() => transaction is null ? ValueTask.CompletedTask : transaction.DisposeAsync();
    }

    [LoggerMessage(
        EventId = 6340,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_run_missing runId={RunId}")]
    private partial void LogRunMissing(Guid runId);

    [LoggerMessage(
        EventId = 6341,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_run_not_bible_proposal runId={RunId} kind={Kind}")]
    private partial void LogRunNotBibleProposal(Guid runId, string kind);

    [LoggerMessage(
        EventId = 6342,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_run_scope_invalid runId={RunId} reason={Reason}")]
    private partial void LogRunScopeInvalid(Guid runId, string reason);

    [LoggerMessage(
        EventId = 6343,
        Level = LogLevel.Information,
        Message = "propose_bible_update_run_terminal runId={RunId} status={Status}")]
    private partial void LogRunTerminal(Guid runId, GenerationRunStatus status);

    [LoggerMessage(
        EventId = 6344,
        Level = LogLevel.Information,
        Message = "propose_bible_update_run_held runId={RunId}")]
    private partial void LogRunHeld(Guid runId);

    [LoggerMessage(
        EventId = 6345,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_series_missing runId={RunId} seriesId={SeriesId}")]
    private partial void LogSeriesMissing(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6346,
        Level = LogLevel.Information,
        Message = "propose_bible_update_series_archived runId={RunId} seriesId={SeriesId}")]
    private partial void LogSeriesArchived(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6347,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_true_crime_autopilot runId={RunId} seriesId={SeriesId}")]
    private partial void LogTrueCrimeAutopilot(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6348,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_episode_missing runId={RunId} episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6349,
        Level = LogLevel.Error,
        Message = "propose_bible_update_bible_missing runId={RunId} seriesId={SeriesId}")]
    private partial void LogBibleMissing(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6350,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_invalid_output runId={RunId} attempt={Attempt}")]
    private partial void LogInvalidOutput(Guid runId, int attempt);

    [LoggerMessage(
        EventId = 6351,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_summary_fallback_unavailable runId={RunId} episodeId={EpisodeId}")]
    private partial void LogSummaryFallbackUnavailable(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6352,
        Level = LogLevel.Information,
        Message = "propose_bible_update_no_ops runId={RunId} episodeId={EpisodeId}")]
    private partial void LogNoOps(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6353,
        Level = LogLevel.Information,
        Message = "propose_bible_update_succeeded runId={RunId} seriesId={SeriesId} episodeId={EpisodeId} fromVersion={FromVersion} toVersion={ToVersion} branch={Branch} modelOps={ModelOps} keptOps={KeptOps} droppedOps={DroppedOps} applied={Applied} skipped={Skipped}")]
    private partial void LogSucceeded(
        Guid runId,
        Guid seriesId,
        Guid episodeId,
        int fromVersion,
        int toVersion,
        string branch,
        int modelOps,
        int keptOps,
        int droppedOps,
        int applied,
        int skipped);

    [LoggerMessage(
        EventId = 6354,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_failed runId={RunId} seriesId={SeriesId} code={Code} detail={Detail}")]
    private partial void LogRunFailed(Guid runId, Guid seriesId, string code, string detail);

    [LoggerMessage(
        EventId = 6355,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_stopped runId={RunId} code={Code}")]
    private partial void LogStopped(Guid runId, string code);

    [LoggerMessage(
        EventId = 6356,
        Level = LogLevel.Error,
        Message = "propose_bible_update_unexpected runId={RunId} error={Error}")]
    private partial void LogUnexpected(Guid runId, string error);

    [LoggerMessage(
        EventId = 6357,
        Level = LogLevel.Information,
        Message = "propose_bible_update_cancelled runId={RunId}")]
    private partial void LogCancelled(Guid runId);

    [LoggerMessage(
        EventId = 6358,
        Level = LogLevel.Warning,
        Message = "propose_bible_update_ledger_failed runId={RunId} operation={Operation} code={Code}")]
    private partial void LogLedgerFailed(Guid runId, string operation, string code);
}
