using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.GenerateFactSheet;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Similarity;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Research.Jobs;

/// <summary>
/// Executes one queued <c>fact_sheet</c> run end to end (02.5.3 plan §7, D21): re-check the sources, plan the
/// input budget, run the free D12 source pre-check, make one paid extraction call (with the single
/// invalid-output retry), validate the facts, then save one draft version and settle the reservation.
/// </summary>
/// <remarks>
/// <para>
/// Stages on <c>generation_runs.stage</c>: <c>running → writing</c> (the plan, the source pre-check and the
/// call) <c>→ checking</c> (the validator) <c>→ saving</c> (the draft, the run outcome
/// and the settlement). The claim is a conditional <c>UPDATE … WHERE id AND status = queued</c> (or
/// <c>running</c> past the run deadline plus the sweep grace) inside a transaction, so two deliveries of the
/// same run cannot both work it, a run another worker holds is left alone, and a run the sweeper already
/// ended is rejected.
/// </para>
/// <para>
/// Every failure path writes the run outcome and the episode's <c>FactSheetGenerationFailed</c> in one
/// transaction (its outbox row follows from the aggregate), then refunds the reservation. Only an
/// <see cref="CancellationToken"/> that is the job's own propagates: a run whose host shuts down stays
/// <c>running</c> and is reclaimed by <c>StaleGenerationSweeperJob</c>, which owns that case. No prompt,
/// source text or fact statement reaches a log, an <c>error_detail</c> or a metric (D19).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model.</param>
/// <param name="clock">The clock.</param>
/// <param name="ledger">The credit ledger: settle on success, refund on every failure.</param>
/// <param name="meter">The only writer of <c>usage_records</c>.</param>
/// <param name="extractor">The paid fact-extraction call.</param>
/// <param name="moderator">The free D12 pre-check of the sources.</param>
/// <param name="moderationEvents">The only writer of <c>moderation_events</c>.</param>
/// <param name="textStore">The extracted-text store a url source's prose lives in.</param>
/// <param name="prices">The price versions; the run's <c>queued_at</c> picks the one it is costed with.</param>
/// <param name="prompts">The prompt catalog; it composes the run's <c>prompt_version</c>.</param>
/// <param name="jobOptions">The run deadline (<c>FactSheet:Job</c>).</param>
/// <param name="extractionOptions">The input budget the plan is built against (<c>Facts</c>).</param>
/// <param name="similaritySettings">The similarity thresholds, including <c>FactOverlapBlock</c> (F18).</param>
/// <param name="logger">The logger.</param>
public sealed partial class GenerateFactSheetPipeline(
    IAppDbContext db,
    IClock clock,
    ICreditLedger ledger,
    IUsageMeter meter,
    IFactExtractor extractor,
    IContentModerator moderator,
    IModerationEventWriter moderationEvents,
    IExtractedTextStore textStore,
    IPriceCatalog prices,
    IPromptCatalog prompts,
    FactSheetJobOptions jobOptions,
    FactSheetExtractionOptions extractionOptions,
    SimilaritySettings similaritySettings,
    ILogger<GenerateFactSheetPipeline> logger)
{
    /// <summary>The D14 usage operation of the one paid call.</summary>
    public const string Operation = "fact_sheet";

    /// <summary>The prompt manifest key of the extraction step.</summary>
    private const string FactsPromptKey = "facts/v1.md";

    /// <summary>The prompt manifest key of the <c>facts.v1</c> output schema.</summary>
    private const string FactsSchemaKey = "schemas/facts.v1.json";

    /// <summary>The detail written for each failure path; safe text, never content (D19).</summary>
    private const string DetailSourcesChanged = "sources_changed";

    private const string DetailSourceTextUnavailable = "source_text_unavailable";
    private const string DetailInputOverBudget = "input_over_budget";
    private const string DetailPriceCatalogEmpty = "price_catalog_empty";
    private const string DetailModerationUnavailable = "moderation_unavailable";
    private const string DetailSourcesFlagged = "sources_flagged";
    private const string DetailProviderRefused = "provider_refused";
    private const string DetailInvalidOutputTwice = "invalid_output_twice";
    private const string DetailFundsExhausted = "funds_exhausted";
    private const string DetailProviderUnavailable = "provider_unavailable";
    private const string DetailProviderError = "provider_error";
    private const string DetailDeadlineExceeded = "deadline_exceeded";
    private const string DetailVersionConflict = "version_conflict";
    private const string DetailUnexpected = "unexpected_error";

    /// <summary>The ledger reason of each refund, the D1 vocabulary of a released reservation.</summary>
    private const string RefundInvalidOutput = "fact_sheet_invalid_output";

    private const string RefundModerated = "fact_sheet_moderated";
    private const string RefundFailed = "fact_sheet_generation_failed";
    private const string RefundUnavailable = "provider_unavailable";
    private const string RefundFundsExhausted = "provider_funds_exhausted";

    /// <summary>The Postgres <c>unique_violation</c> SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// The score a fact gets when the similarity scorer returns something that is not a score (a NaN, an
    /// infinity, or a value outside [0,1]): above every accepted threshold, so the fact is dropped as a
    /// verbatim copy instead of the run failing on a cast.
    /// </summary>
    private const decimal VerbatimCopyScore = 2m;

    /// <summary>The longest detail string written to <c>generation_runs.models</c>.</summary>
    private const int DetailMaxChars = 64;

    /// <summary>The write budget of the terminal write, which must survive the run deadline and a shutdown (class 3).</summary>
    private static readonly TimeSpan TerminalWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The ledger call budget, mirroring <c>StoryGenerationProgress</c>.</summary>
    private static readonly TimeSpan LedgerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The prompt files one extraction call uses; the extractor composes the same parts.</summary>
    private static readonly PromptVersionParts PromptParts = new(FactsPromptKey, FactsPromptKey, FactsSchemaKey);

    /// <summary>
    /// Runs the fact-sheet generation of <paramref name="generationRunId"/> (02.5.3 plan §7).
    /// </summary>
    /// <param name="generationRunId">The run; the only data argument (ids-only rule, D19).</param>
    /// <param name="jobToken">Hangfire's shutdown token: cancelling it leaves the run to the sweeper.</param>
    /// <returns>A task that completes when the run reached a status, or was left to another actor.</returns>
    public async Task RunAsync(Guid generationRunId, CancellationToken jobToken)
    {
        var state = await LoadAsync(generationRunId, jobToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        deadline.CancelAfter(state.Timeout);

        try
        {
            await ExecuteAsync(state, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (jobToken.IsCancellationRequested)
        {
            // Class 3: the host is shutting down. Whatever the stage threw, the run stays running and the
            // sweeper reclaims it, so no write is attempted — the shutdown owns the outcome, not the failure.
            LogCancelled(state.Run.Id);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Only this run's own deadline maps here: an OCE from another token is not a deadline (see below).
            DiscardPendingChanges();
            await FailAsync(state, ErrorCodes.FactSheetGenerationFailed, DetailDeadlineExceeded, RefundFailed).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DiscardPendingChanges();
            LogUnexpected(state.Run.Id, exception.GetType().Name);
            await FailAsync(state, ErrorCodes.FactSheetGenerationFailed, DetailUnexpected, RefundFailed).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the run is still <c>running</c> for another worker, or stale enough for this one.</summary>
    /// <param name="run">The run as loaded.</param>
    /// <returns><see langword="true"/> when the run counts as stale.</returns>
    private bool IsStale(GenerationRun run) =>
        (run.StartedAt ?? run.QueuedAt) + jobOptions.Timeout + StoryGenerationPipeline.SweepGrace <= clock.UtcNow;

    // Loads the run, its episode and its series, and holds every case in which this delivery must do nothing.
    private async Task<RunState?> LoadAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.GenerationRuns
            .SingleOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null)
        {
            LogRunMissing(runId);
            return null;
        }

        if (run.Kind != GenerationKind.FactSheet)
        {
            LogRunNotFactSheet(runId, run.Kind.ToString());
            return null;
        }

        if (run.EpisodeId is not { } episodeId)
        {
            LogRunNotFactSheet(runId, "episode_missing");
            return null;
        }

        if (run.Status is GenerationRunStatus.Succeeded or GenerationRunStatus.Failed or GenerationRunStatus.Cancelled)
        {
            LogRunTerminal(runId, run.Status);
            return null;
        }

        if (run.Status == GenerationRunStatus.Running && !IsStale(run))
        {
            LogRunHeld(runId);
            return null;
        }

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        if (episode is null)
        {
            // The episode was deleted after the run was queued (FK cascade removes the run too, so this is the mid-run case).
            LogEpisodeMissing(runId, episodeId);
            return null;
        }

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == run.SeriesId, ct)
            .ConfigureAwait(false);
        if (series is null)
        {
            LogSeriesMissing(runId, run.SeriesId);
            return null;
        }

        return new RunState(run, episode, series, jobOptions.Timeout);
    }

    private async Task ExecuteAsync(RunState s, CancellationToken ct)
    {
        if (!await ClaimAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await PlanAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        // Plan §7 and README D12: the free source pre-check runs before the paid call, so a flagged source
        // never buys an extraction.
        if (!await ModerateSourcesAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        var draft = await ExtractAsync(s, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return;
        }

        if (!await StageAsync(s, GenerationStage.Checking, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await ValidateAsync(s, draft, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await StageAsync(s, GenerationStage.Saving, ct).ConfigureAwait(false))
        {
            return;
        }

        await SaveAsync(s, ct).ConfigureAwait(false);
    }

    // Step 0: the conditional claim, `Start` and the first stage, in one transaction.
    private async Task<bool> ClaimAsync(RunState s, CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Only a queued run, or a running one its worker stopped reporting on (older than the run deadline plus
        // the sweep grace), is claimable: the whole decision is one UPDATE under the row lock, so two deliveries
        // of the same run cannot both pass it. The earlier load's `held` guard is the fast path; this is the
        // authority, because a run still `running` when this delivery loaded it can be claimed by another.
        var staleBefore = now - s.Timeout - StoryGenerationPipeline.SweepGrace;

        await using var write = await BeginAsync(ct).ConfigureAwait(false);

        // Held under the row lock: a concurrent delivery, the sweeper or an admin ending the run waits for this
        // write and then fails its own claim on the committed state.
        var claimed = await db.GenerationRuns
            .Where(candidate => candidate.Id == s.Run.Id
                && (candidate.Status == GenerationRunStatus.Queued
                    || (candidate.Status == GenerationRunStatus.Running
                        && (candidate.StartedAt ?? candidate.QueuedAt) < staleBefore)))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.UpdatedAt, now)
                    // The claim stamps the takeover instant, so a stale run this delivery just took over is no
                    // longer stale for the next delivery: without it the same stale window would admit a second
                    // claim and a second paid call. A queued run gets the same value `Start` writes below.
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
            LogStopped(s.Run.Id, "claim_rejected");
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

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await write.CommitAsync(ct).ConfigureAwait(false);

        s.Adopt(run);
        return true;
    }

    // Step 0b: the run's own re-check of what it is about to work on, then the input plan. The episode is
    // re-checked by its topic and its editability, and the source set by its fetched rows: the episode's
    // `previous_episode_status` recorded when the run was queued is never compared, so an episode that moved on
    // (or lost its sources) while the run waited fails here as `sources_changed` rather than by a status diff.
    private async Task<bool> PlanAsync(RunState s, CancellationToken ct)
    {
        if (s.Series.Topic != Topic.TrueCrime || !s.Episode.CanEditStory)
        {
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailSourcesChanged, RefundFailed).ConfigureAwait(false);
            return false;
        }

        var sources = await db.StorySources
            .Where(source => source.EpisodeId == s.Episode.Id && source.Status == StorySourceStatus.Fetched)
            .OrderBy(source => source.CreatedAt)
            .ThenBy(source => source.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (sources.Count == 0)
        {
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailSourcesChanged, RefundFailed).ConfigureAwait(false);
            return false;
        }

        var candidates = new List<FactSourceCandidate>(sources.Count);
        foreach (var source in sources)
        {
            var text = await TextAsync(s, source, ct).ConfigureAwait(false);
            if (text is null)
            {
                return false;
            }

            candidates.Add(new FactSourceCandidate(
                source.Id,
                source.Title ?? string.Empty,
                source.Kind == StorySourceKind.UserNote ? FactSourceKind.UserNote : FactSourceKind.Url,
                string.IsNullOrWhiteSpace(text) ? null : text,
                source.CreatedAt));
        }

        var price = await prices.GetActiveAsync(s.Run.QueuedAt, ct).ConfigureAwait(false);
        if (price.IsError)
        {
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailPriceCatalogEmpty, RefundFailed).ConfigureAwait(false);
            return false;
        }

        if (s.Run.ReservationId is not { } reservation)
        {
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailUnexpected, RefundFailed).ConfigureAwait(false);
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
            // The retry rewrites the attempt on a copy of this context; the scope always carries the first attempt.
            Attempt: 1,
            s.PriceVersion);

        var plan = FactInputBudget.Plan(s.Series.Language, candidates, extractionOptions.ToLimits());
        s.Plan = plan;
        s.PromptVersion = prompts.BuildPromptVersion(PromptParts);

        if (!await RecordProvenanceAsync(s, null, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (!plan.WithinBudget)
        {
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailInputOverBudget, RefundFailed).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    // The prose of one source: the row's own note, or the object a url source's extraction was written to.
    private async Task<string?> TextAsync(RunState s, StorySource source, CancellationToken ct)
    {
        if (source.Kind == StorySourceKind.UserNote)
        {
            return source.NoteText ?? string.Empty;
        }

        if (source.ExtractedTextKey is not { } key)
        {
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailSourceTextUnavailable, RefundFailed).ConfigureAwait(false);
            return null;
        }

        var read = await textStore.ReadAsync(key, ct).ConfigureAwait(false);
        if (read.IsError)
        {
            LogSourceTextUnavailable(s.Run.Id, source.Id, read.FirstError.Code);
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailSourceTextUnavailable, RefundFailed).ConfigureAwait(false);
            return null;
        }

        return read.Value;
    }

    // Step 1 (the `writing` stage): one paid call, with the single D7 invalid-output retry.
    private async Task<FactsV1?> ExtractAsync(RunState s, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var request = new FactExtractionRequest(
                s.Context! with { Attempt = attempt },
                s.Series.Language,
                s.Plan!.Sources,
                FactSheetCostEstimator.MaxOutputTokens);

            var result = await extractor.ExtractAsync(request, ct).ConfigureAwait(false);
            await MeterAsync(s, result).ConfigureAwait(false);

            switch (result)
            {
                case TextCallOk<FactsV1> ok:
                    s.Model = TextSafety.CleanModelName(ok.Model);
                    return ok.Draft;

                case TextCallInvalidOutput<FactsV1> invalid:
                    LogInvalidOutput(s.Run.Id, attempt, invalid.Usage is not null);
                    if (attempt == 1)
                    {
                        if (!await RecordAttemptAsync(s, ct).ConfigureAwait(false))
                        {
                            return null;
                        }

                        continue;
                    }

                    await FailAsync(s, ErrorCodes.FactSheetInvalidOutput, DetailInvalidOutputTwice, RefundInvalidOutput).ConfigureAwait(false);
                    return null;

                case TextCallModerated<FactsV1> moderated:
                    await WriteModerationEventAsync(s, moderated.Categories).ConfigureAwait(false);
                    await FailAsync(s, ErrorCodes.FactSheetModerated, DetailProviderRefused, RefundModerated).ConfigureAwait(false);
                    return null;

                case TextCallFundsExhausted<FactsV1>:
                    await FailAsync(s, ErrorCodes.ProviderFundsExhausted, DetailFundsExhausted, RefundFundsExhausted).ConfigureAwait(false);
                    return null;

                case TextCallUnavailable<FactsV1>:
                    await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailProviderUnavailable, RefundUnavailable).ConfigureAwait(false);
                    return null;

                default:
                    await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailProviderError, RefundFailed).ConfigureAwait(false);
                    return null;
            }
        }
    }

    // Step 2 (the `checking` stage): the free D12 check of the sources, before any fact is accepted.
    private async Task<bool> ModerateSourcesAsync(RunState s, CancellationToken ct)
    {
        var texts = s.Plan!.Sources.Select(source => source.Text).ToArray();

        ModerationResult verdict;
        try
        {
            using (ModerationScope.Push(s.Context!))
            {
                verdict = await moderator.CheckAsync(texts, ModerationStage.FactSheet, ct).ConfigureAwait(false);
            }
        }
        catch (ModerationUnavailableException)
        {
            LogModerationUnavailable(s.Run.Id);
            await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailModerationUnavailable, RefundFailed).ConfigureAwait(false);
            return false;
        }

        if (!verdict.Flagged)
        {
            return true;
        }

        await WriteModerationEventAsync(s, verdict.Categories).ConfigureAwait(false);
        await FailAsync(s, ErrorCodes.FactSheetModerated, DetailSourcesFlagged, RefundModerated).ConfigureAwait(false);
        return false;
    }

    // Step 3: the deterministic validation of the model output (T2's own rules).
    private async Task<bool> ValidateAsync(RunState s, FactsV1 draft, CancellationToken ct)
    {
        var existingMaxFactId = await db.FactSheets
            .Where(sheet => sheet.EpisodeId == s.Episode.Id)
            .MaxAsync(sheet => (int?)sheet.MaxFactId, ct)
            .ConfigureAwait(false) ?? 0;

        var sources = new Dictionary<Guid, SourceText>(s.Plan!.Sources.Count);
        foreach (var source in s.Plan!.Sources)
        {
            // F18: the similarity corpus is the url sources' prose only; a user note is never scored against.
            if (!source.IsUserNote)
            {
                sources[source.SourceId] = new SourceText(source.SourceId, source.Text);
            }
        }

        var context = new FactSheetValidationContext(
            s.Plan.Sources.ToDictionary(source => source.Alias, source => source.SourceId, StringComparer.Ordinal),
            existingMaxFactId,
            ToScoreThreshold(similaritySettings.FactOverlapBlock),
            extractionOptions.MaxFacts,
            sources);

        var validated = FactSheetValidator.Validate(draft, context, request => ScoreFact(s.Series.Language, request));
        s.Validation = validated;

        // The save re-reads this watermark under the episode's row lock: a version saved in between must fail the
        // run (F12) instead of letting this one reuse the fact ids it numbered.
        s.ValidatedMaxFactId = existingMaxFactId;

        return await RecordProvenanceAsync(s, validated, ct).ConfigureAwait(false);
    }

    // The 02.5.6 adapter: the statement's overlap against the plan's url-source corpus, fail-closed on a broken
    // double. The validator passes the whole corpus (SEC-5), so a statement copied from any source is scored even
    // when the model cited a different one.
    private decimal ScoreFact(Language language, ScoreFactRequest request)
    {
        var corpus = request.Corpus.Select(source => new CorpusText(source.SourceId, source.Text)).ToArray();
        var score = SimilarityScorer.ScoreFact(request.Statement, corpus, SimilarityOptions.For(language, similaritySettings));

        // A NaN, an infinity or a value outside [0,1] is not a score. Treat it as a verbatim copy so the fact is
        // dropped, rather than letting a cast throw and failing the whole run on an unrelated path.
        if (double.IsNaN(score) || double.IsInfinity(score) || score < 0 || score > 1)
        {
            return VerbatimCopyScore;
        }

        return (decimal)score;
    }

    // The threshold cast of the configured ratio; an unusable value must not silently disable the copy check.
    private static decimal ToScoreThreshold(double threshold)
    {
        if (double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            return 0m;
        }

        return threshold switch
        {
            < 0 => 0m,
            > 1 => 1m,
            _ => (decimal)threshold,
        };
    }

    // Step 4 (the `saving` stage): one transaction for the draft, the run outcome and the episode's event. The
    // version and the id watermark are read inside that transaction, under the episode's row lock: a version
    // saved between the validation and this write must not let this run reuse fact ids (F12) or take a version
    // the other writer already took.
    private async Task SaveAsync(RunState s, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var validation = s.Validation!;

        var outcome = SaveOutcome.Saved;
        FactSheet? sheet = null;

        try
        {
            await using var write = await BeginAsync(ct).ConfigureAwait(false);

            // The row lock first, then the reads (the command handler's order): the episode serialises the
            // version counter and the id watermark of every fact sheet written for it.
            await db.LockEpisodeForUpdateAsync(s.Episode.Id, ct).ConfigureAwait(false);

            var lastVersion = await db.FactSheets
                .Where(candidate => candidate.EpisodeId == s.Episode.Id)
                .MaxAsync(candidate => (int?)candidate.Version, ct)
                .ConfigureAwait(false);

            var storedMaxFactId = await db.FactSheets
                .Where(candidate => candidate.EpisodeId == s.Episode.Id)
                .MaxAsync(candidate => (int?)candidate.MaxFactId, ct)
                .ConfigureAwait(false) ?? 0;

            if (storedMaxFactId != s.ValidatedMaxFactId)
            {
                // A version landed between the validation and this write: its ids are already handed out, so this
                // draft would reuse them (F12). The same typed failure as the version collision below.
                outcome = SaveOutcome.VersionConflict;
            }
            else
            {
                sheet = FactSheet.CreateDraft(
                    s.Run.UserId,
                    s.Episode.Id,
                    (lastVersion ?? 0) + 1,
                    validation.Facts,
                    s.Plan!.Sources.ToDictionary(source => source.Alias, source => source.SourceId, StringComparer.Ordinal),
                    s.Plan.InputTruncated,
                    validation.NewMaxFactId,
                    s.Run.Id,
                    s.PromptVersion,
                    now);

                db.FactSheets.Add(sheet);

                var cost = s.Run.ReservationId is { } reservation
                    ? await db.UsageRecords
                        .Where(record => record.ReservationId == reservation)
                        .SumAsync(record => record.CostUsd, ct)
                        .ConfigureAwait(false)
                    : 0m;

                s.Run.Succeed(cost, null, sheet.Id, now);

                var raised = s.Episode.RaiseFactSheetGenerated(s.Run.Id, sheet.Id, sheet.Version, s.Run.Trigger, now);
                if (raised.IsError)
                {
                    // A saved draft always carries a 1-based version and non-empty ids, so this is a programming
                    // error: the run is failed typed below instead of staying running with its reservation held.
                    LogStopped(s.Run.Id, raised.FirstError.Code);
                    outcome = SaveOutcome.Unhandled;
                }
                else
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await write.CommitAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Another run already claimed this (episode_id, version): the D9 version scheme is beaten by a race,
            // which is a typed failure, never a 500 at the job's log.
            outcome = SaveOutcome.VersionConflict;
        }

        switch (outcome)
        {
            case SaveOutcome.VersionConflict:
                DiscardPendingChanges();
                await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailVersionConflict, RefundFailed).ConfigureAwait(false);
                return;

            case SaveOutcome.Unhandled:
                DiscardPendingChanges();
                await FailAsync(s, ErrorCodes.FactSheetGenerationFailed, DetailUnexpected, RefundFailed).ConfigureAwait(false);
                return;

            default:
                break;
        }

        LogSucceeded(s.Run.Id, sheet!.Id, sheet.Version, validation.Facts.Count, validation.ModelFactCount);
        await SettleAsync(s.Run).ConfigureAwait(false);
    }

    // Step 3 of the progress writer's pattern: write the prompt version and the safe run provenance.
    private async Task<bool> RecordProvenanceAsync(RunState s, FactSheetValidationResult? validation, CancellationToken ct)
    {
        var run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
        if (run is null)
        {
            LogStopped(s.Run.Id, "run_missing");
            return false;
        }

        var models = ModelsJson(s, validation);
        if (!TextSafety.IsPersistableJson(models))
        {
            LogStopped(s.Run.Id, "models_unstorable");
            return false;
        }

        run.RecordProvenance(s.PromptVersion, models, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> RecordAttemptAsync(RunState s, CancellationToken ct)
    {
        var run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
        if (run is null)
        {
            LogStopped(s.Run.Id, "run_missing");
            return false;
        }

        run.RecordAttempt(clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> StageAsync(RunState s, GenerationStage stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var run = await FreshAsync(db.GenerationRuns, s.Run.Id, ct).ConfigureAwait(false);
        if (run is null)
        {
            LogStopped(s.Run.Id, "run_missing");
            return false;
        }

        var advanced = run.AdvanceStage(stage, clock.UtcNow);
        if (advanced.IsError)
        {
            LogStopped(s.Run.Id, advanced.FirstError.Code);
            return false;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    // D14: one usage row per paid call, in its own transaction, so the cost survives a later failure of the run.
    private async Task MeterAsync(RunState s, TextCallResult<FactsV1> result)
    {
        if (!result.MayBeBilled)
        {
            return;
        }

        var bound = s.Run.WorstCaseUsd > 0m ? s.Run.WorstCaseUsd : (decimal?)null;
        if (result.Usage is null && bound is null)
        {
            // Nothing to estimate from: the call is recorded by the provider's invoice, not by this run's bound.
            LogUnmetered(s.Run.Id, result.GetType().Name);
            return;
        }

        try
        {
            // The meter owns its transaction and its own 10 s budget: a cancelled or failed run keeps its cost.
            await meter
                .RecordAsync(result.ToUsageItem(s.Context!, Operation, s.Episode.Id, bound), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Ids and the exception type only: a provider or database message can echo content (D19). Metering is
            // evidence, not control flow, so the run's own outcome is still written — but a lost row is an error,
            // because the platform spend cap is read from usage_records.
            LogMeterFailed(s.Run.Id, s.Run.ReservationId, result.RequestId, exception.GetType().Name);
        }
    }

    private async Task WriteModerationEventAsync(RunState s, IReadOnlyCollection<string> categories)
    {
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        await moderationEvents
            .WriteAsync(s.Run.UserId, s.Episode.Id, ModerationSource.OpenaiText, ModerationStage.FactSheet, categories, ownText: null, terminal.Token)
            .ConfigureAwait(false);
    }

    private async Task FailAsync(RunState s, string code, string detail, string refundReason)
    {
        // A fresh token: the run deadline may have expired and the job token is not the write's to cancel (class 3).
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        var written = false;

        try
        {
            await using (var write = await BeginAsync(terminal.Token).ConfigureAwait(false))
            {
                var run = await FreshAsync(db.GenerationRuns, s.Run.Id, terminal.Token).ConfigureAwait(false);
                if (run is null || run.Status is GenerationRunStatus.Succeeded or GenerationRunStatus.Failed or GenerationRunStatus.Cancelled)
                {
                    // Another actor already ended it: the run is never overwritten and the refund is its own call's.
                    LogStopped(s.Run.Id, "already_ended");
                    return;
                }

                var episode = await FreshAsync(db.Episodes, s.Episode.Id, terminal.Token).ConfigureAwait(false);

                run.Fail(code, detail, countsTowardLimit: false, clock.UtcNow);

                if (episode is not null)
                {
                    var errorCode = GenerationErrorCode.Create(code);
                    if (errorCode.IsError)
                    {
                        LogStopped(s.Run.Id, errorCode.FirstError.Code);
                        return;
                    }

                    var raised = episode.RaiseFactSheetGenerationFailed(run.Id, errorCode.Value, run.Trigger, clock.UtcNow);
                    if (raised.IsError)
                    {
                        LogStopped(s.Run.Id, raised.FirstError.Code);
                        return;
                    }
                }

                await db.SaveChangesAsync(terminal.Token).ConfigureAwait(false);
                await write.CommitAsync(terminal.Token).ConfigureAwait(false);
                written = true;
            }
        }
        catch (Exception exception)
        {
            // Type only: a provider or database message can echo content (D19).
            LogStopped(s.Run.Id, exception.GetType().Name);
        }

        if (!written)
        {
            // The outcome was not recorded, so the run is still open: the sweeper owns it and its refund.
            return;
        }

        LogRunFailed(s.Run.Id, code, detail);
        await RefundAsync(s.Run, refundReason).ConfigureAwait(false);
    }

    // Runs on its own 10 s budget, not on the run's deadline token, so a terminal write never skips it.
    private async Task SettleAsync(GenerationRun run)
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

    // The run's provenance: the prompt version plus the plan's and the validator's counts, never content (D19).
    private string ModelsJson(RunState s, FactSheetValidationResult? validation)
    {
        var plan = s.Plan;
        var limits = extractionOptions.ToLimits();
        var facts = new JsonObject
        {
            ["model"] = TextSafety.CleanModelName(s.Model),
            ["input_truncated"] = plan?.InputTruncated ?? false,
            ["excluded_sources"] = plan?.ExcludedSourceIds.Count ?? 0,
            ["truncated_aliases"] = plan?.TruncatedAliases.Count ?? 0,
            ["estimated_input_tokens"] = plan?.EstimatedTokens ?? 0,
            ["budget"] = new JsonObject
            {
                ["max_sources"] = limits.MaxSources,
                ["per_source_char_cap"] = limits.PerSourceCharCap,
                ["max_input_tokens"] = limits.MaxInputTokens,
                ["max_facts"] = extractionOptions.MaxFacts,
            },
        };

        if (validation is not null)
        {
            facts["model_facts"] = validation.ModelFactCount;
            facts["kept_facts"] = validation.Facts.Count;

            var drops = new JsonObject();
            foreach (var group in validation.Drops
                .GroupBy(drop => drop.Reason, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                drops[Bounded(group.Key)] = group.Count();
            }

            facts["drops"] = drops;
        }

        return new JsonObject { ["facts"] = facts }.ToJsonString();
    }

    // The write transaction of one call; disposing it without a commit rolls the claim and every pending write back.
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

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException database && database.SqlState == UniqueViolation)
            {
                return true;
            }
        }

        return false;
    }

    // A safe, bounded provenance string: C0 controls and lone surrogates out, then cut (a jsonb value refuses a NUL).
    private static string Bounded(string? value)
    {
        var cleaned = TextSafety.Clean(value);
        return cleaned.Length <= DetailMaxChars ? cleaned : cleaned[..DetailMaxChars];
    }

    private DbContext Context =>
        db as DbContext ?? throw new InvalidOperationException("The fact-sheet pipeline needs the EF Core context behind IAppDbContext.");

    /// <summary>How the saving stage ended: one clean save, or one of the two ways it cannot take the version.</summary>
    private enum SaveOutcome
    {
        /// <summary>The draft, the run outcome and the episode's event are committed.</summary>
        Saved,

        /// <summary>The version or the id watermark moved since the validation, or the insert met <c>23505</c>.</summary>
        VersionConflict,

        /// <summary>An internal invariant failed (an unreachable domain guard), so the run is failed typed.</summary>
        Unhandled,
    }

    // The write transaction of one call; disposing it without a commit rolls every pending write back.
    private sealed class WriteScope(IDbContextTransaction? transaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken ct) => transaction is null ? Task.CompletedTask : transaction.CommitAsync(ct);

        public ValueTask DisposeAsync() => transaction is null ? ValueTask.CompletedTask : transaction.DisposeAsync();
    }

    /// <summary>What one run's execution reads and accumulates, so no step re-reads the aggregates.</summary>
    private sealed class RunState(GenerationRun run, Episode episode, SeriesAggregate series, TimeSpan timeout)
    {
        public GenerationRun Run { get; private set; } = run;

        public Episode Episode { get; } = episode;

        public SeriesAggregate Series { get; } = series;

        public TimeSpan Timeout { get; } = timeout;

        public string PriceVersion { get; set; } = string.Empty;

        public string PromptVersion { get; set; } = string.Empty;

        public RunContext? Context { get; set; }

        public FactInputPlan? Plan { get; set; }

        /// <summary>The episode's fact-id watermark as the validation read it (the save re-checks it under the row lock).</summary>
        public int ValidatedMaxFactId { get; set; }

        public FactSheetValidationResult? Validation { get; set; }

        public string? Model { get; set; }

        /// <summary>Takes the claimed instance the write reloaded, so later steps read the committed values.</summary>
        /// <param name="claimed">The run as committed by the claim.</param>
        public void Adopt(GenerationRun claimed) => Run = claimed;
    }

    [LoggerMessage(
        EventId = 6170,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_run_missing runId={RunId}")]
    private partial void LogRunMissing(Guid runId);

    [LoggerMessage(
        EventId = 6171,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_run_not_fact_sheet runId={RunId} kind={Kind}")]
    private partial void LogRunNotFactSheet(Guid runId, string kind);

    [LoggerMessage(
        EventId = 6172,
        Level = LogLevel.Information,
        Message = "generate_fact_sheet_job_run_terminal runId={RunId} status={Status}")]
    private partial void LogRunTerminal(Guid runId, GenerationRunStatus status);

    [LoggerMessage(
        EventId = 6173,
        Level = LogLevel.Information,
        Message = "generate_fact_sheet_job_run_held runId={RunId}")]
    private partial void LogRunHeld(Guid runId);

    [LoggerMessage(
        EventId = 6174,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_episode_missing runId={RunId} episodeId={EpisodeId}")]
    private partial void LogEpisodeMissing(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6175,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_series_missing runId={RunId} seriesId={SeriesId}")]
    private partial void LogSeriesMissing(Guid runId, Guid seriesId);

    [LoggerMessage(
        EventId = 6176,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_source_text_unavailable runId={RunId} sourceId={SourceId} code={Code}")]
    private partial void LogSourceTextUnavailable(Guid runId, Guid sourceId, string code);

    [LoggerMessage(
        EventId = 6177,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_invalid_output runId={RunId} attempt={Attempt} metered={Metered}")]
    private partial void LogInvalidOutput(Guid runId, int attempt, bool metered);

    [LoggerMessage(
        EventId = 6178,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_moderation_unavailable runId={RunId}")]
    private partial void LogModerationUnavailable(Guid runId);

    [LoggerMessage(
        EventId = 6179,
        Level = LogLevel.Error,
        Message = "generate_fact_sheet_job_meter_failed runId={RunId} reservationId={ReservationId} requestId={RequestId} error={Error}")]
    private partial void LogMeterFailed(Guid runId, Guid? reservationId, string? requestId, string error);

    [LoggerMessage(
        EventId = 6180,
        Level = LogLevel.Information,
        Message = "generate_fact_sheet_job_unmetered runId={RunId} result={Result}")]
    private partial void LogUnmetered(Guid runId, string result);

    [LoggerMessage(
        EventId = 6181,
        Level = LogLevel.Information,
        Message = "generate_fact_sheet_job_succeeded runId={RunId} factSheetId={FactSheetId} version={Version} facts={Facts} modelFacts={ModelFacts}")]
    private partial void LogSucceeded(Guid runId, Guid factSheetId, int version, int facts, int modelFacts);

    [LoggerMessage(
        EventId = 6182,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_failed runId={RunId} code={Code} detail={Detail}")]
    private partial void LogRunFailed(Guid runId, string code, string detail);

    [LoggerMessage(
        EventId = 6183,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_stopped runId={RunId} code={Code}")]
    private partial void LogStopped(Guid runId, string code);

    [LoggerMessage(
        EventId = 6184,
        Level = LogLevel.Error,
        Message = "generate_fact_sheet_job_unexpected runId={RunId} error={Error}")]
    private partial void LogUnexpected(Guid runId, string error);

    [LoggerMessage(
        EventId = 6185,
        Level = LogLevel.Information,
        Message = "generate_fact_sheet_job_cancelled runId={RunId}")]
    private partial void LogCancelled(Guid runId);

    [LoggerMessage(
        EventId = 6186,
        Level = LogLevel.Warning,
        Message = "generate_fact_sheet_job_ledger_failed runId={RunId} operation={Operation} code={Code}")]
    private partial void LogLedgerFailed(Guid runId, string operation, string code);
}
