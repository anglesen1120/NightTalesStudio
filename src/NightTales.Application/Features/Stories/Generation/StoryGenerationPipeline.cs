using System.Data.Common;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Jobs;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Application.Features.Stories.Review;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Quality;
using NightTales.Domain.Stories.Review;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>
/// Executes one queued story run end to end in D21's order (02.2.2-T3, 02.6.2-T1): step 0 fact-sheet re-check, 0b brief
/// moderation, 1 write (one invalid-output retry), 2 validate, 3 draft moderation, 4 review, 5 the single revision pass
/// (revise → steps 2–4 once more → review #2 → drift), 6 scoring, 7 save + settle. Every state change goes through
/// <see cref="StoryGenerationProgress"/>; every moderation call is wrapped in <see cref="ModerationScope"/> so it is
/// metered; no prompt, brief or story text reaches a log or <c>error_detail</c> (D19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deadline.</b> An independent <see cref="CancellationTokenSource"/> (timeout from <see cref="StoryJobOptions"/>) is
/// linked to the job token. A cancellation caused by the deadline ends the run as <c>story_generation_failed</c>, not
/// counted and refunded, and that terminal write uses its own fresh token: the expired deadline token is never reused.
/// </para>
/// <para>
/// <b>Revision (02.6.2-T1, D21 step 5).</b> When review #1 holds a blocking objective issue, <see cref="RevisionPolicy"/>
/// approves exactly one pass: the revise call is validated, moderated and reviewed again (review #2) before its output can
/// become current. A spend cap at the revision gate, an invalid revise output or an unusable review #2 degrades — the
/// generated row is kept with <c>revision.applied=false</c> — rather than failing the run; only a moderated revision or an
/// exhausted provider account ends it. Review #2 never triggers a second revision.
/// </para>
/// </remarks>
public sealed partial class StoryGenerationPipeline(
    IAppDbContext db,
    StoryGenerationProgress progress,
    StoryWriteRequestFactory requests,
    IFactSheetGuard factSheetGuard,
    IContentModerator moderator,
    IModerationEventWriter moderationEvents,
    IStoryWriter writer,
    IPromptAssembler assembler,
    IBudgetGuard budgetGuard,
    IStoryCostEstimator estimator,
    StoryQualityChecker checker,
    StoryJobOptions jobOptions,
    StoryReviewOptions reviewOptions,
    StoryRevisionOptions revisionOptions,
    ISimilarityCorpusProvider similarityCorpus,
    SimilaritySettings similaritySettings,
    StoryGenerationMetrics metrics,
    IClock clock,
    ILogger<StoryGenerationPipeline> logger)
{
    /// <summary>How long a terminal write may take; independent of the run deadline and of the job token.</summary>
    public static readonly TimeSpan TerminalWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The grace the sweeper adds to the deadline before it takes a running run (plan §7.3).</summary>
    public static readonly TimeSpan SweepGrace = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan SimilarityReadTimeout = TimeSpan.FromSeconds(10);

    private const string AuditSchemaVersion = "audit.v1";
    private const int MaxRuleIds = 8;
    private const double SoftTimeoutShare = 0.8;

    private static readonly Regex RuleIdPattern = new("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions AuditWriteOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Runs <paramref name="generationRunId"/> if it is still ours to run; a terminal or owned run is a no-op.</summary>
    /// <param name="generationRunId">The run (ids only, D19).</param>
    /// <param name="jobToken">The job's cancellation token (worker shutdown).</param>
    /// <returns>A task that completes when the run reached a status, was held, or was left to another worker.</returns>
    public async Task RunAsync(Guid generationRunId, CancellationToken jobToken)
    {
        var run = await db.GenerationRuns.SingleOrDefaultAsync(candidate => candidate.Id == generationRunId, jobToken).ConfigureAwait(false);
        if (run is null)
        {
            LogRunMissing(generationRunId);
            return;
        }

        if (run.Kind != GenerationKind.Story || run.EpisodeId is not { } episodeId)
        {
            LogRunSkipped(run.Id, "not_a_story_run");
            return;
        }

        if (run.Status is GenerationRunStatus.Succeeded or GenerationRunStatus.Failed or GenerationRunStatus.Cancelled)
        {
            LogRunSkipped(run.Id, "already_terminal");
            return;
        }

        var episode = await db.Episodes.SingleOrDefaultAsync(candidate => candidate.Id == episodeId, jobToken).ConfigureAwait(false);
        var series = await db.Series.SingleOrDefaultAsync(candidate => candidate.Id == run.SeriesId, jobToken).ConfigureAwait(false);
        if (episode is null || series is null)
        {
            LogRunSkipped(run.Id, "episode_or_series_missing");
            return;
        }

        var timeout = jobOptions.TimeoutFor(episode.LengthTier);

        if (run.Status == GenerationRunStatus.Running)
        {
            // Crash semantics: a fresh running run belongs to another worker; a stale one is the sweeper's. Never re-enqueued here.
            var owned = run.StartedAt is { } started && started + timeout + SweepGrace > clock.UtcNow;
            LogRunSkipped(run.Id, owned ? "owned_by_another_worker" : "stale_left_to_sweeper");
            return;
        }

        var state = new RunState(run, episode, series.Topic, series.Language, timeout, reviewOptions.Enabled);

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobToken, deadline.Token);

        try
        {
            await ExecuteAsync(state, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !jobToken.IsCancellationRequested)
        {
            progress.DiscardPendingChanges();
            LogDeadlineExceeded(run.Id);
            await FailAsync(state, ErrorCodes.StoryGenerationFailed, "deadline_exceeded", false, RefundFailed).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            // The worker is stopping. The run stays running; the sweeper takes it after the deadline (plan §7.3).
            LogInterrupted(run.Id);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !jobToken.IsCancellationRequested)
        {
            // Includes a database failure at step 7 and a cancellation that neither token caused. The type only: a provider or
            // database message can echo content (D19).
            progress.DiscardPendingChanges();
            LogUnexpected(run.Id, exception.GetType().Name);
            await FailAsync(state, ErrorCodes.StoryGenerationFailed, "unexpected_error", false, RefundFailed).ConfigureAwait(false);
        }
        finally
        {
            FlushStageDuration(state);
        }
    }

    private const string RefundInvalidOutput = "story_invalid_output";
    private const string RefundModerated = "story_moderated";
    private const string RefundFailed = "story_generation_failed";
    private const string RefundUnavailable = "provider_unavailable";

    private async Task ExecuteAsync(RunState s, CancellationToken ct)
    {
        if (!await PrepareAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await ModerateBriefAsync(s, ct).ConfigureAwait(false))
        {
            return;
        }

        var written = await WriteAsync(s, ct).ConfigureAwait(false);
        if (written is null)
        {
            return;
        }

        if (!await CheckAsync(s, written, ct).ConfigureAwait(false))
        {
            return;
        }

        var review = await ReviewAsync(s, written, ct).ConfigureAwait(false);
        if (review is null)
        {
            return;
        }

        // Step 5 (02.6.2-T1): the single revision pass, decided from review #1. A null outcome ended the run.
        var revision = await ReviseAsync(s, written, review, ct).ConfigureAwait(false);
        if (revision is null)
        {
            return;
        }

        // Step 6: source similarity is advisory; corpus failures do not fail generation. Every saved row is scored on
        // its own content, so a revised run scores the written draft and the revision separately.
        if (!await StageAsync(s, GenerationStage.Scoring, ct).ConfigureAwait(false))
        {
            return;
        }

        var hasUrlSources = await db.StorySources
            .AnyAsync(source => source.EpisodeId == s.Episode.Id && source.Kind == StorySourceKind.Url, ct)
            .ConfigureAwait(false);

        var similarity = await ScoreSimilarityAsync(s, written.Draft, s.LocalIssues, ct).ConfigureAwait(false);

        // The local checker runs once per saved row, on that row's own draft and similarity (02.6.2-T1 step 6).
        var originalReport = CheckDraft(s, written.Draft, similarity, hasUrlSources, s.ModerationCategories, review.Validated is null);
        RevisionAudit? revised = null;
        if (revision.Applied is { } applied)
        {
            var scored = await ScoreSimilarityAsync(s, applied.Draft, applied.LocalIssues, ct).ConfigureAwait(false);
            var report = CheckDraft(s, applied.Draft, scored, hasUrlSources, applied.ModerationCategories, review.Validated is null);

            // Drift is advisory only: it never fails the run and never blocks story_review (02.6.2-T1 step 5).
            var drift = SegmentDriftDetector.Detect(
                written.Draft,
                applied.Draft,
                applied.AddressedSegments,
                revisionOptions.MinTextSimilarity,
                BlockingRules(originalReport),
                BlockingRules(report),
                s.Language);
            LogRevisionApplied(s.Run.Id, applied.FixedIssueIds.Count, drift.HasDrift);
            revised = new RevisionAudit(report, scored, drift);
        }

        // Step 7: saving.
        if (!await StageAsync(s, GenerationStage.Saving, ct).ConfigureAwait(false))
        {
            return;
        }

        // The revision model and the revise prompt segment belong to the run, not to the generated row (D5, D22).
        if (revision.ReviseVersion is { } reviseVersion)
        {
            var recorded = await progress
                .RecordProvenanceAsync(
                    s.Run.Id,
                    ComposePromptVersion(written.PromptVersion, review.PromptVersion, reviseVersion),
                    ModelsJson(s),
                    ct)
                .ConfigureAwait(false);
            if (recorded.IsError)
            {
                LogStopped(s.Run.Id, recorded.FirstError.Code);
                return;
            }
        }

        var latest = await db.StoryVersions
            .Where(version => version.EpisodeId == s.Episode.Id)
            .MaxAsync(version => (int?)version.Version, ct)
            .ConfigureAwait(false);
        var version = (latest ?? 0) + 1;

        // The generated row: the audit of the written draft, plus the not-applied marker when the revision degraded.
        var generatedAudit = BuildAudit(s, originalReport, s.LocalIssues, s.ModerationNode, similarity, review);
        if (revision.NotAppliedReason is { } notAppliedReason)
        {
            generatedAudit = AuditReportBuilder.MergeRevisionNotApplied(generatedAudit, notAppliedReason);
        }

        var created = StoryVersion.Create(
            s.Run.UserId,
            s.Episode.Id,
            version,
            StoryVersionSource.Generated,
            written.Draft,
            written.ContentJson,
            written.Model,
            ComposePromptVersion(written.PromptVersion, review.PromptVersion),
            s.Run.Id,
            parentVersionId: null,
            s.Run.InputFactSheetId,
            generatedAudit,
            similarityScore: similarity.Score,
            clock.UtcNow);
        db.StoryVersions.Add(created);

        var versionIds = new List<Guid> { created.Id };
        var currentVersionId = created.Id;

        // The revised row (02.6.2-T1): only when the revision passed validation, moderation and review #2. It becomes
        // the episode's current version; the generated row stays as its parent, readable in the history.
        if (revision.Applied is { } rewritten && revised is { } revisedAudit)
        {
            var revisedAuditJson = AuditReportBuilder.MergeRevision(
                BuildAudit(s, revisedAudit.Report, rewritten.LocalIssues, rewritten.ModerationNode, revisedAudit.Similarity, review),
                created.Id,
                rewritten.FixedIssueIds,
                rewritten.ReviewAfter,
                revisedAudit.Drift);

            var revisedRow = StoryVersion.Create(
                s.Run.UserId,
                s.Episode.Id,
                version + 1,
                StoryVersionSource.Revised,
                rewritten.Draft,
                rewritten.ContentJson,
                rewritten.Model,
                ComposePromptVersion(written.PromptVersion, review.PromptVersion, rewritten.ReviseVersion),
                s.Run.Id,
                parentVersionId: created.Id,
                s.Run.InputFactSheetId,
                revisedAuditJson,
                similarityScore: revisedAudit.Similarity.Score,
                clock.UtcNow);
            db.StoryVersions.Add(revisedRow);
            versionIds.Add(revisedRow.Id);
            currentVersionId = revisedRow.Id;
        }

        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        var completed = await progress
            .CompleteAsync(new StoryCompletion(s.Run.Id, versionIds, currentVersionId), terminal.Token)
            .ConfigureAwait(false);
        if (completed.IsError)
        {
            LogStopped(s.Run.Id, completed.FirstError.Code);
        }
    }

    // Step 0 + request + start.
    private async Task<bool> PrepareAsync(RunState s, CancellationToken ct)
    {
        var sheet = await factSheetGuard.EnsureApprovedAsync(s.Episode.Id, s.Topic, ct).ConfigureAwait(false);
        if (sheet.IsError || sheet.Value.FactSheetId != s.Run.InputFactSheetId)
        {
            await FailAsync(s, ErrorCodes.StoryGenerationFailed, "fact_sheet_outdated", false, RefundFailed).ConfigureAwait(false);
            return false;
        }

        s.Sheet = sheet.Value;

        var built = await requests
            .BuildAsync(s.Run, s.Episode, s.Topic, s.Language, s.Facts, attempt: 0, violatedRules: null, ct)
            .ConfigureAwait(false);
        if (built.IsError)
        {
            await FailAsync(s, ErrorCodes.StoryGenerationFailed, "request_unavailable", false, RefundFailed).ConfigureAwait(false);
            return false;
        }

        s.Built = built.Value;
        s.Rules = new StoryRules
        {
            Length = built.Value.Request.Length,
            Language = s.Language,
            AllowedFactRefs = s.Sheet.Facts.Select(fact => fact.Id).ToHashSet(StringComparer.Ordinal),
        };

        var started = await progress.StartAsync(s.Run.Id, ct).ConfigureAwait(false);
        if (started.IsError)
        {
            LogStopped(s.Run.Id, started.FirstError.Code);
            return false;
        }

        s.Track(GenerationStage.Writing);
        return true;
    }

    // Step 0b: the brief, before any paid call.
    private async Task<bool> ModerateBriefAsync(RunState s, CancellationToken ct)
    {
        var texts = NonBlank([s.Episode.Brief, s.Episode.Title]);
        if (texts.Count == 0)
        {
            return true;
        }

        var verdict = await ModerateAsync(s, texts, ModerationStage.Brief, ct).ConfigureAwait(false);
        switch (verdict.Kind)
        {
            case VerdictKind.Clean:
                return true;
            case VerdictKind.Unavailable:
                await FailAsync(s, ErrorCodes.StoryGenerationFailed, "moderation_unavailable", false, RefundFailed).ConfigureAwait(false);
                return false;
            default:
                await WriteModerationEventAsync(s, ModerationStage.Brief, verdict.Categories, s.Episode.Brief).ConfigureAwait(false);
                await FailAsync(s, ErrorCodes.StoryModerated, "brief_flagged", true, RefundModerated).ConfigureAwait(false);
                return false;
        }
    }

    // Steps 1 and 2 (the persistence-safety part of the validation).
    private async Task<Written?> WriteAsync(RunState s, CancellationToken ct)
    {
        var attempt = 0;
        IReadOnlyList<string>? correction = null;

        while (true)
        {
            if (!await GateAsync(s, TextStep.Writer, ct).ConfigureAwait(false))
            {
                return null;
            }

            var built = await requests
                .BuildAsync(s.Run, s.Episode, s.Topic, s.Language, s.Facts, attempt, correction, ct)
                .ConfigureAwait(false);
            if (built.IsError)
            {
                await FailAsync(s, ErrorCodes.StoryGenerationFailed, "request_unavailable", false, RefundFailed).ConfigureAwait(false);
                return null;
            }

            s.Built = built.Value;

            var result = await writer.WriteAsync(built.Value.Request, ct).ConfigureAwait(false);
            IReadOnlyList<string> invalid;

            switch (result)
            {
                case TextCallOk<StoryContentV1> ok:
                    var unsafeRules = PersistenceRules(s, ok.Draft, out var contentJson);
                    if (unsafeRules.Count == 0)
                    {
                        return await AcceptAsync(s, ok, contentJson, ct).ConfigureAwait(false);
                    }

                    invalid = unsafeRules;
                    break;
                case TextCallInvalidOutput<StoryContentV1> bad:
                    invalid = SafeRuleIds(bad.RuleIds);
                    break;
                case TextCallModerated<StoryContentV1> moderated:
                    await WriteModerationEventAsync(s, ModerationStage.Draft, moderated.Categories, null).ConfigureAwait(false);
                    await FailAsync(s, ErrorCodes.StoryModerated, "writer_refused", true, RefundModerated).ConfigureAwait(false);
                    return null;
                case TextCallFundsExhausted<StoryContentV1>:
                    await PauseAsync(s, GenerationPauseReason.ProviderFundsExhausted).ConfigureAwait(false);
                    return null;
                case TextCallUnavailable<StoryContentV1>:
                    await FailAsync(s, ErrorCodes.StoryGenerationFailed, "provider_unavailable", false, RefundUnavailable).ConfigureAwait(false);
                    return null;
                default:
                    // A provider error and any outcome this code does not know: fail closed, never continue.
                    await FailAsync(s, ErrorCodes.StoryGenerationFailed, "provider_error", false, RefundFailed).ConfigureAwait(false);
                    return null;
            }

            metrics.InvalidOutput();

            if (attempt > 0)
            {
                await FailAsync(s, ErrorCodes.StoryInvalidOutput, "invalid_output_twice", false, RefundInvalidOutput).ConfigureAwait(false);
                return null;
            }

            attempt = 1;
            correction = invalid;

            var counted = await progress.RecordAttemptAsync(s.Run.Id, ct).ConfigureAwait(false);
            if (counted.IsError)
            {
                LogStopped(s.Run.Id, counted.FirstError.Code);
                return null;
            }
        }
    }

    private async Task<Written?> AcceptAsync(RunState s, TextCallOk<StoryContentV1> ok, string contentJson, CancellationToken ct)
    {
        var request = s.Built.Request;
        var prompt = assembler.Assemble(
            request,
            new AssemblerContext(s.Topic, s.Language, StoryPresetKeys.For(s.Topic, s.Language), request.Length.WriterMaxOutput));

        var model = TextSafety.CleanModelName(ok.Model);
        s.WriterModel = model;

        var recorded = await progress
            .RecordProvenanceAsync(s.Run.Id, prompt.PromptVersion, ModelsJson(s), ct)
            .ConfigureAwait(false);
        if (recorded.IsError)
        {
            LogStopped(s.Run.Id, recorded.FirstError.Code);
            return null;
        }

        return new Written(ok.Draft, contentJson, model, prompt.PromptVersion);
    }

    // Steps 2 and 3: validate (issues go to audit.v1.local.issues), then moderate the draft. Quality runs at BuildAudit.
    private async Task<bool> CheckAsync(RunState s, Written written, CancellationToken ct)
    {
        if (!await StageAsync(s, GenerationStage.Checking, ct).ConfigureAwait(false))
        {
            return false;
        }

        var validation = StoryDraftValidator.Validate(written.Draft, s.Rules);
        foreach (var issue in validation.Issues)
        {
            s.LocalIssues.Add(IssueNode(
                issue.RuleId,
                issue.Severity == IssueSeverity.Blocking ? "blocking" : "advisory",
                issue.Kind == IssueKind.Objective ? "objective" : "taste",
                issue.Segment,
                issue.Evidence,
                issue.RepairDirection));
        }

        if (!await StageAsync(s, GenerationStage.Checking, ct).ConfigureAwait(false))
        {
            return false;
        }

        var derived = StoryVersion.Derive(written.Draft);
        var texts = NonBlank([written.Draft.Title, derived.Body, written.Draft.Description, .. written.Draft.Tags ?? []]);

        var verdict = await ModerateAsync(s, texts, ModerationStage.Draft, ct).ConfigureAwait(false);
        switch (verdict.Kind)
        {
            case VerdictKind.Unavailable:
                await FailAsync(s, ErrorCodes.StoryGenerationFailed, "moderation_unavailable", false, RefundFailed).ConfigureAwait(false);
                return false;
            case VerdictKind.Flagged:
                // D12: a blocking category is recorded and blocks approval downstream; it does not fail the run.
                await WriteModerationEventAsync(s, ModerationStage.Draft, verdict.Categories, null).ConfigureAwait(false);
                var flagged = Moderation(verdict.Categories, flagged: true);
                s.ModerationCategories = flagged.Categories;
                s.ModerationNode = flagged.Node;
                return true;
            default:
                var clean = Moderation([], flagged: false);
                s.ModerationCategories = clean.Categories;
                s.ModerationNode = clean.Node;
                return true;
        }
    }

    // The moderation block of one saved row (D12): a flagged verdict with no category falls back to the unspecified
    // category, which is itself a blocking one. Returns the block and the categories the row records.
    private static (JsonObject Node, IReadOnlyList<string> Categories) Moderation(IReadOnlyList<string> categories, bool flagged)
    {
        var effective = flagged && categories.Count == 0
            ? new[] { StoryQualityChecker.UnspecifiedModerationCategory }
            : categories.ToArray();
        var node = new JsonObject
        {
            ["flagged"] = flagged,
            ["categories"] = new JsonArray([.. effective.Select(category => (JsonNode)TextSafety.Clean(category))]),
            ["blocking"] = new JsonArray([.. effective
                .Where(StoryQualityChecker.BlockingModerationCategories.Contains)
                .Select(category => (JsonNode)TextSafety.Clean(category))]),
            ["stale"] = false,
            ["unavailable"] = false,
        };

        return (node, effective);
    }

    // Step 4 (02.6.1-T1 steps 4-5).
    private async Task<ReviewOutcome?> ReviewAsync(RunState s, Written written, CancellationToken ct)
    {
        if (!s.ReviewEnabled)
        {
            return ReviewOutcome.Skipped;
        }

        if (!await StageAsync(s, GenerationStage.Reviewing, ct).ConfigureAwait(false))
        {
            return null;
        }

        var questions = EarRuleQuestions.For(s.Language);
        var questionIds = questions.Select(question => question.QuestionId).ToArray();
        var reviewAttempt = 0;

        while (true)
        {
            if (!await GateAsync(s, TextStep.Review, ct).ConfigureAwait(false))
            {
                return null;
            }

            var request = new StoryReviewRequest(
                s.Built.Request.Run with { Attempt = reviewAttempt },
                written.Draft,
                s.Language,
                s.Episode.LengthTier,
                s.Built.Request.Length,
                s.Topic,
                s.Sheet.Facts,
                Bible: null,
                questions,
                StoryPresetKeys.For(s.Topic, s.Language));

            var assembled = assembler.Review(request);
            if (assembled.IsError)
            {
                await FailAsync(s, ErrorCodes.StoryGenerationFailed, "review_prompt_unavailable", false, RefundFailed).ConfigureAwait(false);
                return null;
            }

            var result = await writer.ReviewAsync(request, ct).ConfigureAwait(false);

            switch (result)
            {
                case TextCallOk<StoryReviewV1> ok:
                    var validated = ReviewResultValidator.Validate(ok.Draft, s.Topic, written.Draft.Paragraphs?.Count ?? 0, questionIds);
                    if (validated.IsAccepted)
                    {
                        var model = TextSafety.CleanModelName(ok.Model);
                        s.ReviewModel = model;
                        var recorded = await progress
                            .RecordProvenanceAsync(
                                s.Run.Id,
                                ComposePromptVersion(written.PromptVersion, assembled.Value.PromptVersion),
                                ModelsJson(s),
                                ct)
                            .ConfigureAwait(false);
                        if (recorded.IsError)
                        {
                            LogStopped(s.Run.Id, recorded.FirstError.Code);
                            return null;
                        }

                        return new ReviewOutcome(SanitizeReview(validated), model, assembled.Value.PromptVersion);
                    }

                    break;
                case TextCallInvalidOutput<StoryReviewV1>:
                    break;
                case TextCallModerated<StoryReviewV1> moderated:
                    await WriteModerationEventAsync(s, ModerationStage.Draft, moderated.Categories, null).ConfigureAwait(false);
                    await FailAsync(s, ErrorCodes.StoryModerated, "review_refused", true, RefundModerated).ConfigureAwait(false);
                    return null;
                case TextCallFundsExhausted<StoryReviewV1>:
                    await PauseAsync(s, GenerationPauseReason.ProviderFundsExhausted).ConfigureAwait(false);
                    return null;
                case TextCallUnavailable<StoryReviewV1>:
                    await FailAsync(s, ErrorCodes.StoryGenerationFailed, "provider_unavailable", false, RefundUnavailable).ConfigureAwait(false);
                    return null;
                default:
                    await FailAsync(s, ErrorCodes.StoryGenerationFailed, "provider_error", false, RefundFailed).ConfigureAwait(false);
                    return null;
            }

            // An invalid or rejected review: one retry, then story_invalid_output (02.6.1-T1 step 5).
            metrics.InvalidOutput();

            if (reviewAttempt > 0)
            {
                await FailAsync(s, ErrorCodes.StoryInvalidOutput, "review_invalid_twice", false, RefundInvalidOutput).ConfigureAwait(false);
                return null;
            }

            reviewAttempt = 1;
        }
    }

    // Step 5 (02.6.2-T1, D21 step 5): at most one revision pass, and only for a review that holds a blocking objective
    // issue. A null result means the run already ended (a moderated revision or an exhausted provider account); a result
    // with no applied revision keeps the written draft, with the reason recorded in the row's audit.
    private async Task<RevisionOutcome?> ReviseAsync(RunState s, Written written, ReviewOutcome review, CancellationToken ct)
    {
        if (review.Validated is not { } validated)
        {
            return RevisionOutcome.Skip;
        }

        // D21: MaxRevisions is a hard bound of exactly one pass; a disabled stage or a zero count never revises.
        var maxRevisions = revisionOptions.Enabled ? revisionOptions.MaxRevisions : 0;
        if (!RevisionPolicy.ShouldRevise(validated.Review, maxRevisions, revisionAlreadyApplied: false))
        {
            return RevisionOutcome.Skip;
        }

        var issues = RevisionPolicy.BlockingObjectiveIssues(validated.Review);

        if (!await StageAsync(s, GenerationStage.Revising, ct).ConfigureAwait(false))
        {
            return null;
        }

        switch (await GateOptionalAsync(s, TextStep.Revision, ct).ConfigureAwait(false))
        {
            case GateOutcome.BoundUnavailable:
                return null;
            case GateOutcome.SpendCap:
                // The spend cap between the review and the revision degrades: the generated row is saved, the
                // revision is recorded as not applied, and the episode still reaches story_review.
                return NotApplied(s, AuditReportBuilder.RevisionReasonSpendCapReached);
        }

        var request = new StoryRevisionRequest(
            s.Built.Request.Run with { Attempt = 0 },
            written.Draft,
            validated.Review,
            issues,
            s.Language,
            s.Episode.LengthTier,
            s.Built.Request.Length,
            s.Topic,
            s.Sheet.Facts,
            Bible: null,
            StoryPresetKeys.For(s.Topic, s.Language));

        var assembled = assembler.Revise(request);
        if (assembled.IsError)
        {
            // The D5 bound rejected the revision prompt: the original is kept.
            return NotApplied(s, AuditReportBuilder.RevisionReasonUnavailable);
        }

        var reviseVersion = assembled.Value.PromptVersion;

        switch (await writer.ReviseAsync(request, ct).ConfigureAwait(false))
        {
            case TextCallOk<StoryContentV1> ok:
                return await AcceptRevisionAsync(s, written, review, ok, issues, reviseVersion, ct).ConfigureAwait(false);
            case TextCallInvalidOutput<StoryContentV1>:
                return InvalidRevision(s, reviseVersion);
            case TextCallModerated<StoryContentV1> moderated:
                await WriteModerationEventAsync(s, ModerationStage.Draft, moderated.Categories, null).ConfigureAwait(false);
                await FailAsync(s, ErrorCodes.StoryModerated, "revision_refused", true, RefundModerated).ConfigureAwait(false);
                return null;
            case TextCallFundsExhausted<StoryContentV1>:
                await PauseAsync(s, GenerationPauseReason.ProviderFundsExhausted).ConfigureAwait(false);
                return null;
            default:
                // A provider error and any outcome this code does not know: the revision is optional, so the original
                // is kept (fail closed: an unknown output never becomes current).
                return NotApplied(s, AuditReportBuilder.RevisionReasonUnavailable, reviseVersion);
        }
    }

    // Steps 2–4 for the revised draft: validate, moderate and review it again, in that order, before it can become the
    // episode's current version. Review #2 never triggers another revision (D21), and an invalid or unreviewed revision
    // keeps the written draft.
    private async Task<RevisionOutcome?> AcceptRevisionAsync(
        RunState s,
        Written written,
        ReviewOutcome review,
        TextCallOk<StoryContentV1> ok,
        IReadOnlyList<ReviewIssueV1> issues,
        string reviseVersion,
        CancellationToken ct)
    {
        s.RevisionModel = TextSafety.CleanModelName(ok.Model);

        // Step 2: the persistence rule and the validator of the written draft. A revised draft that adds a blocking
        // finding the original did not have (a dropped or invented fact_ref, a malformed field) is a regression and is
        // never applied; the revision call itself is not retried (Goal: exactly one revision call).
        var unsafeRules = PersistenceRules(s, ok.Draft, out var contentJson);
        var validation = StoryDraftValidator.Validate(ok.Draft, s.Rules);
        var originalBlocking = StoryDraftValidator.Validate(written.Draft, s.Rules).BlockingRuleIds.ToHashSet(StringComparer.Ordinal);
        if (unsafeRules.Count > 0 || validation.BlockingRuleIds.Any(rule => !originalBlocking.Contains(rule)))
        {
            return InvalidRevision(s, reviseVersion);
        }

        if (!await StageAsync(s, GenerationStage.Checking, ct).ConfigureAwait(false))
        {
            return null;
        }

        var localIssues = new List<JsonNode>();
        foreach (var issue in validation.Issues)
        {
            localIssues.Add(IssueNode(
                issue.RuleId,
                issue.Severity == IssueSeverity.Blocking ? "blocking" : "advisory",
                issue.Kind == IssueKind.Objective ? "objective" : "taste",
                issue.Segment,
                issue.Evidence,
                issue.RepairDirection));
        }

        if (!await StageAsync(s, GenerationStage.Checking, ct).ConfigureAwait(false))
        {
            return null;
        }

        // Step 3: the revised draft is untrusted — the same moderator, over the same text set as the written draft (D9).
        var derived = StoryVersion.Derive(ok.Draft);
        var texts = NonBlank([ok.Draft.Title, derived.Body, ok.Draft.Description, .. ok.Draft.Tags ?? []]);
        var verdict = await ModerateAsync(s, texts, ModerationStage.Draft, ct).ConfigureAwait(false);

        JsonObject moderationNode;
        IReadOnlyList<string> moderationCategories;
        switch (verdict.Kind)
        {
            case VerdictKind.Unavailable:
                // Fail closed: a revision that cannot be moderated never becomes current.
                return NotApplied(s, AuditReportBuilder.RevisionReasonUnavailable, reviseVersion);
            case VerdictKind.Flagged:
                // D12: a blocking category is recorded and blocks approval downstream; it does not fail the run.
                await WriteModerationEventAsync(s, ModerationStage.Draft, verdict.Categories, null).ConfigureAwait(false);
                (moderationNode, moderationCategories) = Moderation(verdict.Categories, flagged: true);
                break;
            default:
                (moderationNode, moderationCategories) = Moderation([], flagged: false);
                break;
        }

        // Review #2: the same review step over the revision, one call, metered as story_review, never a revision trigger.
        var reviewAfter = await ReviewRevisionAsync(s, ok.Draft, ct).ConfigureAwait(false);
        if (reviewAfter.Validated is not { } validatedReview)
        {
            // No reason means the run already ended (the cost bound failed, or the account is out of funds).
            return reviewAfter.NotAppliedReason is { } reason
                ? NotApplied(s, reason, reviseVersion)
                : null;
        }

        return RevisionOutcome.AppliedRevision(new Revision(
            ok.Draft,
            contentJson,
            s.RevisionModel,
            reviseVersion,
            validatedReview,
            [.. issues.Select(issue => issue.Id)],
            AddressedSegments([.. issues.Select(issue => issue.Segment)]),
            moderationNode,
            moderationCategories,
            localIssues));
    }

    // Review #2 of the revised draft: the same gate, validator and sanitiser as review #1, but exactly one call — a
    // single unusable review makes the revision unusable, and it never triggers another revision (D21). Only an
    // exhausted provider account (and a cost bound the estimator cannot produce) ends the run.
    private async Task<ReviewAfterOutcome> ReviewRevisionAsync(RunState s, StoryContentV1 draft, CancellationToken ct)
    {
        switch (await GateOptionalAsync(s, TextStep.Review, ct).ConfigureAwait(false))
        {
            case GateOutcome.BoundUnavailable:
                return ReviewAfterOutcome.RunEnded;
            case GateOutcome.SpendCap:
                return ReviewAfterOutcome.Unusable;
        }

        if (!await StageAsync(s, GenerationStage.Reviewing, ct).ConfigureAwait(false))
        {
            return ReviewAfterOutcome.RunEnded;
        }

        var questions = EarRuleQuestions.For(s.Language);
        var questionIds = questions.Select(question => question.QuestionId).ToArray();
        var request = new StoryReviewRequest(
            s.Built.Request.Run with { Attempt = 0 },
            draft,
            s.Language,
            s.Episode.LengthTier,
            s.Built.Request.Length,
            s.Topic,
            s.Sheet.Facts,
            Bible: null,
            questions,
            StoryPresetKeys.For(s.Topic, s.Language));

        // The D5 bound applies to the larger revised draft too; a prompt that cannot be assembled is an unusable review.
        if (assembler.Review(request).IsError)
        {
            return ReviewAfterOutcome.Unusable;
        }

        switch (await writer.ReviewAsync(request, ct).ConfigureAwait(false))
        {
            case TextCallOk<StoryReviewV1> ok:
                var validated = ReviewResultValidator.Validate(ok.Draft, s.Topic, draft.Paragraphs?.Count ?? 0, questionIds);
                if (!validated.IsAccepted)
                {
                    metrics.InvalidOutput();
                    return ReviewAfterOutcome.Unusable;
                }

                return ReviewAfterOutcome.Usable(SanitizeReview(validated));
            case TextCallInvalidOutput<StoryReviewV1>:
                metrics.InvalidOutput();
                return ReviewAfterOutcome.Unusable;
            case TextCallFundsExhausted<StoryReviewV1>:
                await PauseAsync(s, GenerationPauseReason.ProviderFundsExhausted).ConfigureAwait(false);
                return ReviewAfterOutcome.RunEnded;
            default:
                return ReviewAfterOutcome.Unusable;
        }
    }

    private RevisionOutcome InvalidRevision(RunState s, string reviseVersion)
    {
        metrics.InvalidOutput();
        return NotApplied(s, AuditReportBuilder.RevisionReasonInvalidOutput, reviseVersion);
    }

    private RevisionOutcome NotApplied(RunState s, string reason, string? reviseVersion = null)
    {
        LogRevisionNotApplied(s.Run.Id, reason);
        return RevisionOutcome.NotApplied(reason, reviseVersion);
    }

    // The D22 addresses the revision was asked to change: the segments of the blocking objective issues. A blank one is
    // dropped (fail closed: it must not widen the addressed set and thereby hide drift).
    private static IReadOnlyCollection<string> AddressedSegments(IReadOnlyCollection<string> segments) =>
        [.. segments.Where(segment => !string.IsNullOrWhiteSpace(segment))];

    // The local checker's blocking findings of one draft: the drift check compares them across the two drafts.
    private static IReadOnlyCollection<string> BlockingRules(QualityReport report) =>
        [.. report.Issues.Where(issue => issue.Severity == QualitySeverity.Blocking).Select(issue => issue.RuleId)];

    private async Task<SimilarityOutcome> ScoreSimilarityAsync(RunState s, StoryContentV1 draft, List<JsonNode> localIssues, CancellationToken ct)
    {
        var options = SimilarityOptions.For(s.Language, similaritySettings);
        var started = Stopwatch.GetTimestamp();
        IReadOnlyList<CorpusText> corpus;
        using var deadline = new CancellationTokenSource(SimilarityReadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            corpus = await similarityCorpus.LoadAsync(s.Episode.Id, s.Language, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (SimilarityCorpusUnavailableException exception)
        {
            ct.ThrowIfCancellationRequested();
            return Unavailable(exception.Reason);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Unavailable(SimilarityUnavailableReason.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            var reason = HasDatabaseCause(exception)
                ? SimilarityUnavailableReason.DbError
                : SimilarityUnavailableReason.StorageError;
            if (reason == SimilarityUnavailableReason.DbError)
            {
                LogSimilarityReadError(s.Episode.Id, s.Run.Id, reason, exception.GetType().Name);
            }
            else
            {
                LogSimilarityUnexpectedError(s.Episode.Id, s.Run.Id, reason, exception.GetType().FullName ?? exception.GetType().Name);
            }

            return Unavailable(reason);
        }

        ct.ThrowIfCancellationRequested();
        var story = NightTales.Domain.Stories.Similarity.StoryText.From(draft);
        if (!SimilarityScorer.IsScorable(story, corpus, options))
        {
            return new SimilarityOutcome(null, SimilarityVerdict.Pass, null);
        }

        var result = SimilarityScorer.Score(story, corpus, options);
        ct.ThrowIfCancellationRequested();
        var stored = (decimal)Math.Round(result.Score, 4, MidpointRounding.AwayFromZero);
        var duration = Stopwatch.GetElapsedTime(started);
        metrics.SimilarityScored(result.Corpus, (double)stored, duration.TotalSeconds);
        var segmentsOverWarn = result.Segments.Count(segment => segment.Share > options.WarnAbove);
        LogSimilarityScored(s.Episode.Id, s.Run.Id, result.Corpus, result.N, stored, segmentsOverWarn, duration.TotalMilliseconds);
        return new SimilarityOutcome(stored, SimilarityApprovalGuard.Classify((double)stored, options), result);

        SimilarityOutcome Unavailable(string reason)
        {
            metrics.SimilarityUnavailable(reason);
            LogSimilarityUnavailable(s.Episode.Id, s.Run.Id, reason);
            s.LocalIssues.Add(IssueNode("similarity_unavailable", "advisory", "objective", SegmentAddress.Meta, string.Empty, string.Empty));
            return new SimilarityOutcome(null, SimilarityVerdict.Pass, null);
        }
    }

    private static bool HasDatabaseCause(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is DbException)
            {
                return true;
            }
        }

        return false;
    }

    // The local checker report of one draft, computed on that draft's own content (02.6.2-T1 step 6): each saved row
    // carries the report of the draft it describes, and the drift check compares two of them.
    private QualityReport CheckDraft(
        RunState s,
        StoryContentV1 draft,
        SimilarityOutcome similarity,
        bool hasUrlSources,
        IReadOnlyList<string>? moderationCategories,
        bool modelReviewStale) =>
        checker.Check(
            draft,
            new StoryCheckContext(
                s.Topic,
                s.Episode.LengthTier,
                s.Built.Request.Length,
                s.Rules,
                s.Sheet.Facts.Count,
                [],
                hasUrlSources,
                (double?)similarity.Score,
                similarity.Verdict,
                moderationCategories,
                ModelReviewStale: modelReviewStale,
                null));

    private static string BuildAudit(
        RunState s,
        QualityReport report,
        IReadOnlyList<JsonNode> localIssues,
        JsonObject? moderationNode,
        SimilarityOutcome similarity,
        ReviewOutcome review)
    {
        var local = new JsonArray([.. localIssues]);
        foreach (var issue in report.ToAuditIssues())
        {
            local.Add(IssueNode(issue.Rule, issue.Severity, issue.Kind, issue.Segment, issue.Evidence, issue.RepairDirection));
        }

        var root = new JsonObject
        {
            ["schema_version"] = AuditSchemaVersion,
            ["local"] = new JsonObject { ["issues"] = local },
            ["moderation"] = moderationNode ?? new JsonObject { ["stale"] = true, ["unavailable"] = true },
        };
        if (similarity.Result is { } result)
        {
            root["similarity"] = (JsonNode)result.ToAuditSection();
        }

        string json;
        if (review.Validated is { } validated)
        {
            json = AuditReportBuilder.MergeModelReview(root.ToJsonString(AuditWriteOptions), validated, review.Model!, review.PromptVersion!);
        }
        else
        {
            root["model_review"] = new JsonObject { ["scores"] = null, ["stale"] = true };
            json = root.ToJsonString(AuditWriteOptions);
        }

        if (!TextSafety.IsPersistableJson(json))
        {
            throw new InvalidOperationException("The audit report cannot be stored.");
        }

        return json;
    }

    // Class 1/4: nothing from the model reaches jsonb uncleaned, and the merged review is capped by ReviewResultValidator.
    private static ValidatedReview SanitizeReview(ValidatedReview validated)
    {
        var review = validated.Review;

        return validated with
        {
            Review = review with
            {
                Issues = [.. review.Issues.Select(issue => issue with
                {
                    Evidence = TextSafety.Clean(issue.Evidence),
                    Impact = TextSafety.Clean(issue.Impact),
                    RepairDirection = TextSafety.Clean(issue.RepairDirection),
                })],
                RealWorldClaims = [.. review.RealWorldClaims.Select(claim => claim with { Evidence = TextSafety.Clean(claim.Evidence) })],
                EarRuleAnswers = [.. review.EarRuleAnswers.Select(answer => answer with { Evidence = TextSafety.Clean(answer.Evidence) })],
            },
        };
    }

    private static JsonObject IssueNode(string rule, string severity, string kind, string segment, string evidence, string? repairDirection) =>
        new JsonObject
        {
            ["rule"] = TextSafety.Clean(rule),
            ["severity"] = severity,
            ["kind"] = kind,
            ["segment"] = TextSafety.Clean(segment),
            ["evidence"] = TextSafety.Clean(evidence),
            ["repair_direction"] = TextSafety.Clean(repairDirection),
        };

    // Rule ids that make a draft unstorable (NUL, lone surrogate); everything else is a local issue, not a failure.
    private static List<string> PersistenceRules(RunState s, StoryContentV1 draft, out string contentJson)
    {
        var rules = new List<string>();

        try
        {
            contentJson = StoryContentJson.Write(draft, Array.Empty<FactSourceRef>());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException)
        {
            contentJson = string.Empty;
            rules.Add(StoryDraftValidator.ControlCharactersInvalid);
            return rules;
        }

        if (!TextSafety.IsPersistableJson(contentJson)
            || StoryDraftValidator.Validate(draft, s.Rules).BlockingRuleIds.Contains(StoryDraftValidator.ControlCharactersInvalid))
        {
            rules.Add(StoryDraftValidator.ControlCharactersInvalid);
        }

        return rules;
    }

    private static IReadOnlyList<string> SafeRuleIds(IReadOnlyList<string>? ruleIds) =>
        [.. (ruleIds ?? []).Where(id => RuleIdPattern.IsMatch(id)).Distinct(StringComparer.Ordinal).Take(MaxRuleIds)];

    // D5 composite: the writer's composite plus the review and revision segments, placed before schema= in the order
    // review then revise (README D5: rel=…;sys=writer.v1;preset=…;review=v1;revise=v1;schema=story.v1).
    private static string ComposePromptVersion(string writerVersion, string? reviewVersion, string? reviseVersion = null)
    {
        var parts = writerVersion.Split(';').ToList();
        var changed = AddSegment(parts, reviewVersion, "review=");
        changed |= AddSegment(parts, reviseVersion, "revise=");

        return changed ? string.Join(';', parts) : writerVersion;
    }

    private static bool AddSegment(List<string> parts, string? version, string prefix)
    {
        var segment = version?
            .Split(';')
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.Ordinal));
        if (segment is null || parts.Contains(segment, StringComparer.Ordinal))
        {
            return false;
        }

        var schema = parts.FindIndex(part => part.StartsWith("schema=", StringComparison.Ordinal));
        parts.Insert(schema < 0 ? parts.Count : schema, segment);

        return true;
    }

    private static List<string> NonBlank(IEnumerable<string?> texts) =>
        [.. texts.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!)];

    private async Task<Verdict> ModerateAsync(RunState s, IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct)
    {
        try
        {
            using (ModerationScope.Push(s.Built.Request.Run))
            {
                var result = await moderator.CheckAsync(texts, stage, ct).ConfigureAwait(false);

                // Fail closed: a flag with no category, or categories without a flag, is a flag.
                return result.Flagged || result.Categories.Count > 0
                    ? new Verdict(VerdictKind.Flagged, result.Categories)
                    : new Verdict(VerdictKind.Clean, []);
            }
        }
        catch (ModerationUnavailableException)
        {
            return new Verdict(VerdictKind.Unavailable, []);
        }
    }

    private async Task WriteModerationEventAsync(RunState s, ModerationStage stage, IReadOnlyCollection<string> categories, string? ownText)
    {
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        await moderationEvents
            .WriteAsync(s.Run.UserId, s.Episode.Id, ModerationSource.OpenaiText, stage, categories, ownText, terminal.Token)
            .ConfigureAwait(false);
    }

    private async Task<bool> GateAsync(RunState s, TextStep step, CancellationToken ct)
    {
        var bound = step == TextStep.Review
            ? await estimator.ReviewWorstCaseAsync(s.Episode.LengthTier, s.Language, ct).ConfigureAwait(false)
            : await estimator.WriterWorstCaseAsync(s.Episode.LengthTier, s.Language, ct).ConfigureAwait(false);
        if (bound.IsError)
        {
            await FailAsync(s, ErrorCodes.StoryGenerationFailed, "cost_bound_unavailable", false, RefundFailed).ConfigureAwait(false);
            return false;
        }

        var allowed = await budgetGuard.CheckAsync(new CostBound(bound.Value.WorstCaseUsd), ct, s.Run.Id).ConfigureAwait(false);
        if (allowed.IsError)
        {
            await PauseAsync(s, GenerationPauseReason.SpendCapReached).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    // The gate for the optional revision steps (02.6.2-T1, D21 step 5): the revision bound for the revision call and
    // the review bound for review #2. Unlike GateAsync it never pauses — a spend cap here degrades, so the generated
    // row is still saved, the reason is recorded and the episode still reaches story_review.
    private async Task<GateOutcome> GateOptionalAsync(RunState s, TextStep step, CancellationToken ct)
    {
        var bound = step == TextStep.Review
            ? await estimator.ReviewWorstCaseAsync(s.Episode.LengthTier, s.Language, ct).ConfigureAwait(false)
            : await estimator.RevisionWorstCaseAsync(s.Episode.LengthTier, s.Language, ct).ConfigureAwait(false);
        if (bound.IsError)
        {
            await FailAsync(s, ErrorCodes.StoryGenerationFailed, "cost_bound_unavailable", false, RefundFailed).ConfigureAwait(false);
            return GateOutcome.BoundUnavailable;
        }

        var allowed = await budgetGuard.CheckAsync(new CostBound(bound.Value.WorstCaseUsd), ct, s.Run.Id).ConfigureAwait(false);

        return allowed.IsError ? GateOutcome.SpendCap : GateOutcome.Proceed;
    }

    private async Task<bool> StageAsync(RunState s, GenerationStage stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var recorded = await progress.RecordStageAsync(s.Run.Id, stage, ct).ConfigureAwait(false);
        if (recorded.IsError)
        {
            LogStopped(s.Run.Id, recorded.FirstError.Code);
            return false;
        }

        s.Track(stage);
        FlushStageDuration(s);
        s.Stage = stage;
        s.StageStarted = Stopwatch.GetTimestamp();

        var elapsed = Stopwatch.GetElapsedTime(s.StartedAt);
        if (!s.SoftWarned && elapsed >= s.Timeout * SoftTimeoutShare)
        {
            s.SoftWarned = true;
            metrics.SoftTimeout();
            LogSoftTimeout(s.Run.Id, stage, elapsed.TotalSeconds);
        }

        return true;
    }

    private void FlushStageDuration(RunState s)
    {
        if (s.Stage is not { } stage)
        {
            return;
        }

        metrics.StageDuration(Wire(s.Episode.LengthTier), Wire(stage), Stopwatch.GetElapsedTime(s.StageStarted).TotalSeconds);
        s.Stage = null;
    }

    private async Task FailAsync(RunState s, string code, string detail, bool counted, string refundReason)
    {
        // A fresh token: the run deadline may have expired, and the job token is not the write's to cancel (class 3).
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        var failed = await progress
            .FailAsync(s.Run.Id, new StoryFailure(code, detail, counted, refundReason), terminal.Token)
            .ConfigureAwait(false);

        if (failed.IsError)
        {
            LogStopped(s.Run.Id, failed.FirstError.Code);
            return;
        }

        LogRunFailed(s.Run.Id, code, detail);
    }

    private async Task PauseAsync(RunState s, GenerationPauseReason reason)
    {
        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        var paused = await progress.PauseAsync(s.Run.Id, reason, terminal.Token).ConfigureAwait(false);

        if (paused.IsError)
        {
            LogStopped(s.Run.Id, paused.FirstError.Code);
            return;
        }

        LogRunPaused(s.Run.Id, reason);
    }

    private string ModelsJson(RunState s)
    {
        var models = new JsonObject { ["review_enabled"] = s.ReviewEnabled, ["revision_enabled"] = revisionOptions.Enabled };

        if (s.WriterModel is not null)
        {
            models["writer"] = TextSafety.Clean(s.WriterModel);
        }

        if (s.ReviewModel is not null)
        {
            models["review"] = TextSafety.Clean(s.ReviewModel);
        }

        if (s.RevisionModel is not null)
        {
            models["revision"] = TextSafety.Clean(s.RevisionModel);
        }

        return models.ToJsonString();
    }

    private static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed record SimilarityOutcome(decimal? Score, SimilarityVerdict Verdict, SimilarityResult? Result);

    private sealed record Written(StoryContentV1 Draft, string ContentJson, string Model, string PromptVersion);

    private sealed record ReviewOutcome(ValidatedReview? Validated, string? Model, string? PromptVersion)
    {
        public static ReviewOutcome Skipped { get; } = new(null, null, null);
    }

    // One applied revision pass (02.6.2-T1, D22): the material of the revised row, its review #2 and the addresses the
    // revision was asked to change (the fixed issue ids and their segments).
    private sealed record Revision(
        StoryContentV1 Draft,
        string ContentJson,
        string Model,
        string ReviseVersion,
        ValidatedReview ReviewAfter,
        IReadOnlyList<string> FixedIssueIds,
        IReadOnlyCollection<string> AddressedSegments,
        JsonObject ModerationNode,
        IReadOnlyList<string> ModerationCategories,
        List<JsonNode> LocalIssues);

    private sealed record RevisionOutcome(Revision? Applied, string? NotAppliedReason, string? ReviseVersion)
    {
        public static RevisionOutcome Skip { get; } = new(null, null, null);

        public static RevisionOutcome NotApplied(string reason, string? reviseVersion) => new(null, reason, reviseVersion);

        public static RevisionOutcome AppliedRevision(Revision revision) => new(revision, null, revision.ReviseVersion);
    }

    // Review #2's result: a usable review, an unusable one (the revision is then not applied), or the run already ended.
    private sealed record ReviewAfterOutcome(ValidatedReview? Validated, string? NotAppliedReason)
    {
        public static ReviewAfterOutcome RunEnded { get; } = new(null, null);

        public static ReviewAfterOutcome Unusable { get; } = new(null, AuditReportBuilder.RevisionReasonReviewUnavailable);

        public static ReviewAfterOutcome Usable(ValidatedReview validated) => new(validated, null);
    }

    // The local check, similarity and drift of an applied revision, computed once per run (02.6.2-T1 step 6).
    private sealed record RevisionAudit(QualityReport Report, SimilarityOutcome Similarity, RevisionDrift Drift);

    private enum GateOutcome
    {
        Proceed,
        BoundUnavailable,
        SpendCap,
    }

    private enum VerdictKind
    {
        Clean,
        Flagged,
        Unavailable,
    }

    private readonly record struct Verdict(VerdictKind Kind, IReadOnlyList<string> Categories);

    private sealed class RunState(GenerationRun run, Episode episode, Topic topic, Language language, TimeSpan timeout, bool reviewEnabled)
    {
        public GenerationRun Run { get; } = run;

        public Episode Episode { get; } = episode;

        public Topic Topic { get; } = topic;

        public Language Language { get; } = language;

        public TimeSpan Timeout { get; } = timeout;

        public bool ReviewEnabled { get; } = reviewEnabled;

        public ApprovedFactSheet Sheet { get; set; } = ApprovedFactSheet.None;

        public IReadOnlyList<FactForWriter>? Facts => Topic.IsFiction() ? null : Sheet.Facts;

        public BuiltWriteRequest Built { get; set; } = null!;

        public StoryRules Rules { get; set; } = new();

        public string? WriterModel { get; set; }

        public string? ReviewModel { get; set; }

        public string? RevisionModel { get; set; }

        public List<JsonNode> LocalIssues { get; } = [];

        public JsonObject? ModerationNode { get; set; }

        public IReadOnlyList<string>? ModerationCategories { get; set; }

        public long StartedAt { get; } = Stopwatch.GetTimestamp();

        public bool SoftWarned { get; set; }

        public GenerationStage? Stage { get; set; }

        public long StageStarted { get; set; } = Stopwatch.GetTimestamp();

        public void Track(GenerationStage stage)
        {
            if (Stage is null)
            {
                Stage = stage;
                StageStarted = Stopwatch.GetTimestamp();
            }
        }
    }

    [LoggerMessage(EventId = 6110, Level = LogLevel.Warning, Message = "generation_run_missing runId={RunId}")]
    private partial void LogRunMissing(Guid runId);

    [LoggerMessage(EventId = 6111, Level = LogLevel.Information, Message = "generation_run_skipped runId={RunId} reason={Reason}")]
    private partial void LogRunSkipped(Guid runId, string reason);

    [LoggerMessage(EventId = 6112, Level = LogLevel.Warning, Message = "generation_run_failed runId={RunId} code={Code} detail={Detail}")]
    private partial void LogRunFailed(Guid runId, string code, string detail);

    [LoggerMessage(EventId = 6113, Level = LogLevel.Information, Message = "generation_run_paused runId={RunId} reason={Reason}")]
    private partial void LogRunPaused(Guid runId, GenerationPauseReason reason);

    [LoggerMessage(EventId = 6114, Level = LogLevel.Warning, Message = "generation_run_stopped runId={RunId} code={Code}")]
    private partial void LogStopped(Guid runId, string code);

    [LoggerMessage(EventId = 6115, Level = LogLevel.Warning, Message = "generation_deadline_exceeded runId={RunId}")]
    private partial void LogDeadlineExceeded(Guid runId);

    [LoggerMessage(EventId = 6116, Level = LogLevel.Warning, Message = "generation_interrupted runId={RunId}")]
    private partial void LogInterrupted(Guid runId);

    [LoggerMessage(EventId = 6117, Level = LogLevel.Error, Message = "generation_unexpected_error runId={RunId} exceptionType={ExceptionType}")]
    private partial void LogUnexpected(Guid runId, string exceptionType);

    [LoggerMessage(EventId = 6118, Level = LogLevel.Warning, Message = "generation_soft_timeout runId={RunId} stage={Stage} seconds={Seconds:F1}")]
    private partial void LogSoftTimeout(Guid runId, GenerationStage stage, double seconds);

    [LoggerMessage(EventId = 6119, Level = LogLevel.Information, Message = "generation_revision_applied runId={RunId} fixedIssues={FixedIssueCount} drift={Drift}")]
    private partial void LogRevisionApplied(Guid runId, int fixedIssueCount, bool drift);

    [LoggerMessage(EventId = 6124, Level = LogLevel.Information, Message = "generation_revision_not_applied runId={RunId} reason={Reason}")]
    private partial void LogRevisionNotApplied(Guid runId, string reason);

    [LoggerMessage(EventId = 6120, Level = LogLevel.Warning, Message = "similarity_unavailable episodeId={EpisodeId} runId={RunId} reason={Reason}")]
    private partial void LogSimilarityUnavailable(Guid episodeId, Guid runId, string reason);

    [LoggerMessage(EventId = 6121, Level = LogLevel.Warning, Message = "similarity_read_error episodeId={EpisodeId} runId={RunId} reason={Reason} exceptionType={ExceptionType}")]
    private partial void LogSimilarityReadError(Guid episodeId, Guid runId, string reason, string exceptionType);

    [LoggerMessage(EventId = 6122, Level = LogLevel.Information, Message = "similarity_scored episodeId={EpisodeId} runId={RunId} corpus={Corpus} n={N} score={Score} segmentsOverWarn={SegmentsOverWarn} durationMs={DurationMs}")]
    private partial void LogSimilarityScored(Guid episodeId, Guid runId, string corpus, int n, decimal score, int segmentsOverWarn, double durationMs);

    [LoggerMessage(EventId = 6123, Level = LogLevel.Error, Message = "similarity_unexpected_error episodeId={EpisodeId} runId={RunId} reason={Reason} exceptionType={ExceptionType}")]
    private partial void LogSimilarityUnexpectedError(Guid episodeId, Guid runId, string reason, string exceptionType);
}
