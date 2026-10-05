using System.Data.Common;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// Executes one queued story run end to end in D21's order (02.2.2-T3): step 0 fact-sheet re-check, 0b brief moderation,
/// 1 write (one invalid-output retry), 2 validate, 3 draft moderation, 4 review, 6 scoring, 7 save + settle. Every state
/// change goes through <see cref="StoryGenerationProgress"/>; every moderation call is wrapped in
/// <see cref="ModerationScope"/> so it is metered; no prompt, brief or story text reaches a log or <c>error_detail</c> (D19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deadline.</b> An independent <see cref="CancellationTokenSource"/> (timeout from <see cref="StoryJobOptions"/>) is
/// linked to the job token. A cancellation caused by the deadline ends the run as <c>story_generation_failed</c>, not
/// counted and refunded, and that terminal write uses its own fresh token: the expired deadline token is never reused.
/// </para>
/// <para>
/// <b>Hand-overs.</b> Step 5 (revision + review #2) belongs to 02.6.2-T1: it is inserted in <c>ReviewAsync</c> where the
/// blocking-and-objective issue count is taken (see <c>02.6.2-T1</c> marker).
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
    ISimilarityCorpusProvider similarityCorpus,
    IOptions<SimilaritySettings> similaritySettings,
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

        // Step 6: source similarity is advisory; corpus failures do not fail generation.
        if (!await StageAsync(s, GenerationStage.Scoring, ct).ConfigureAwait(false))
        {
            return;
        }

        var similarity = await ScoreSimilarityAsync(s, written, ct).ConfigureAwait(false);

        var hasUrlSources = await db.StorySources
            .AnyAsync(source => source.EpisodeId == s.Episode.Id && source.Kind == StorySourceKind.Url, ct)
            .ConfigureAwait(false);
        var audit = BuildAudit(s, written, review, hasUrlSources, similarity);

        // Step 7: saving.
        if (!await StageAsync(s, GenerationStage.Saving, ct).ConfigureAwait(false))
        {
            return;
        }

        var latest = await db.StoryVersions
            .Where(version => version.EpisodeId == s.Episode.Id)
            .MaxAsync(version => (int?)version.Version, ct)
            .ConfigureAwait(false);

        var promptVersion = ComposePromptVersion(written.PromptVersion, review.PromptVersion);

        var created = StoryVersion.Create(
            s.Run.UserId,
            s.Episode.Id,
            (latest ?? 0) + 1,
            StoryVersionSource.Generated,
            written.Draft,
            written.ContentJson,
            written.Model,
            promptVersion,
            s.Run.Id,
            parentVersionId: null,
            s.Run.InputFactSheetId,
            audit,
            similarityScore: similarity.Score,
            clock.UtcNow);
        db.StoryVersions.Add(created);

        using var terminal = new CancellationTokenSource(TerminalWriteTimeout);
        var completed = await progress
            .CompleteAsync(new StoryCompletion(s.Run.Id, [created.Id], created.Id), terminal.Token)
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
                var blocking = verdict.Categories.Count == 0
                    ? new[] { StoryQualityChecker.UnspecifiedModerationCategory }
                    : verdict.Categories.Where(StoryQualityChecker.BlockingModerationCategories.Contains).ToArray();
                await WriteModerationEventAsync(s, ModerationStage.Draft, verdict.Categories, null).ConfigureAwait(false);
                s.ModerationCategories = verdict.Categories.Count == 0 ? new[] { StoryQualityChecker.UnspecifiedModerationCategory } : verdict.Categories.ToArray();
                s.ModerationNode = new JsonObject
                {
                    ["flagged"] = true,
                    ["categories"] = new JsonArray([.. s.ModerationCategories.Select(category => (JsonNode)TextSafety.Clean(category))]),
                    ["blocking"] = new JsonArray([.. blocking.Select(category => (JsonNode)TextSafety.Clean(category))]),
                    ["stale"] = false,
                    ["unavailable"] = false,
                };
                return true;
            default:
                s.ModerationCategories = [];
                s.ModerationNode = new JsonObject
                {
                    ["flagged"] = false,
                    ["categories"] = new JsonArray(),
                    ["blocking"] = new JsonArray(),
                    ["stale"] = false,
                    ["unavailable"] = false,
                };
                return true;
        }
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

                        var blockingObjective = validated.Review.Issues.Count(
                            issue => issue.Severity == "blocking" && ReviewIssueTypes.KindOf(issue.Type) == IssueKind.Objective);
                        if (blockingObjective > 0)
                        {
                            // 02.6.2-T1: step 5 (ReviseAsync) and review #2 are inserted here. Until then the generated
                            // version is kept (the "Revision Reject" row), the issues stay in model_review, and no
                            // `revising` stage is recorded (02 §10 T113(b)).
                            LogRevisionUnavailable(s.Run.Id, blockingObjective);
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

    private async Task<SimilarityOutcome> ScoreSimilarityAsync(RunState s, Written written, CancellationToken ct)
    {
        var options = SimilarityOptions.For(s.Language, similaritySettings.Value);
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
        var story = NightTales.Domain.Stories.Similarity.StoryText.From(written.Draft);
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

    private string BuildAudit(RunState s, Written written, ReviewOutcome review, bool hasUrlSources, SimilarityOutcome similarity)
    {
        var report = checker.Check(
            written.Draft,
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
                s.ModerationCategories,
                ModelReviewStale: review.Validated is null,
                null));

        var local = new JsonArray([.. s.LocalIssues]);
        foreach (var issue in report.ToAuditIssues())
        {
            local.Add(IssueNode(issue.Rule, issue.Severity, issue.Kind, issue.Segment, issue.Evidence, issue.RepairDirection));
        }

        var root = new JsonObject
        {
            ["schema_version"] = AuditSchemaVersion,
            ["local"] = new JsonObject { ["issues"] = local },
            ["moderation"] = s.ModerationNode ?? new JsonObject { ["stale"] = true, ["unavailable"] = true },
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

    // D5 composite for a run that reviewed: the writer's composite plus the review segment, placed before schema= (README D5:
    // rel=…;sys=writer.v1;preset=…;review=v1;schema=story.v1). No revise= segment: no revision pass exists yet (02.6.2-T1).
    private static string ComposePromptVersion(string writerVersion, string? reviewVersion)
    {
        var reviewSegment = reviewVersion?
            .Split(';')
            .FirstOrDefault(part => part.StartsWith("review=", StringComparison.Ordinal));
        if (reviewSegment is null)
        {
            return writerVersion;
        }

        var parts = writerVersion.Split(';').ToList();
        if (parts.Contains(reviewSegment, StringComparer.Ordinal))
        {
            return writerVersion;
        }

        var schema = parts.FindIndex(part => part.StartsWith("schema=", StringComparison.Ordinal));
        parts.Insert(schema < 0 ? parts.Count : schema, reviewSegment);

        return string.Join(';', parts);
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

    private static string ModelsJson(RunState s)
    {
        var models = new JsonObject { ["review_enabled"] = s.ReviewEnabled };

        if (s.WriterModel is not null)
        {
            models["writer"] = TextSafety.Clean(s.WriterModel);
        }

        if (s.ReviewModel is not null)
        {
            models["review"] = TextSafety.Clean(s.ReviewModel);
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

    [LoggerMessage(EventId = 6119, Level = LogLevel.Information, Message = "generation_revision_unavailable runId={RunId} blockingObjectiveIssues={Count}")]
    private partial void LogRevisionUnavailable(Guid runId, int count);

    [LoggerMessage(EventId = 6120, Level = LogLevel.Warning, Message = "similarity_unavailable episodeId={EpisodeId} runId={RunId} reason={Reason}")]
    private partial void LogSimilarityUnavailable(Guid episodeId, Guid runId, string reason);

    [LoggerMessage(EventId = 6121, Level = LogLevel.Warning, Message = "similarity_read_error episodeId={EpisodeId} runId={RunId} reason={Reason} exceptionType={ExceptionType}")]
    private partial void LogSimilarityReadError(Guid episodeId, Guid runId, string reason, string exceptionType);

    [LoggerMessage(EventId = 6122, Level = LogLevel.Information, Message = "similarity_scored episodeId={EpisodeId} runId={RunId} corpus={Corpus} n={N} score={Score} segmentsOverWarn={SegmentsOverWarn} durationMs={DurationMs}")]
    private partial void LogSimilarityScored(Guid episodeId, Guid runId, string corpus, int n, decimal score, int segmentsOverWarn, double durationMs);

    [LoggerMessage(EventId = 6123, Level = LogLevel.Error, Message = "similarity_unexpected_error episodeId={EpisodeId} runId={RunId} reason={Reason} exceptionType={ExceptionType}")]
    private partial void LogSimilarityUnexpectedError(Guid episodeId, Guid runId, string reason, string exceptionType);
}
