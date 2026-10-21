using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The story half of the auto-approval gate (02.8.2 plan §3.1, §4 UC1–UC3): it loads one autopilot run's episode,
/// series and current story version from stored data, evaluates the fifteen checks (<see cref="StoryGateRules"/>),
/// records the verdict on <c>autopilot_runs.gate_results</c> and, on a live pass, approves the story through the
/// same <c>ApproveStoryCommand</c> a user calls, as a system actor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only, and never a paid call.</b> The audit report, the stored content, the stored similarity score, the
/// series' approved fact sheet and its style template are all that the checks read; the one outbound call is the
/// free <c>autopilot_gate</c> moderation check of check 7, which 02.8.2-T2 runs over the current narration, the
/// metadata and the expanded scene prompts on every evaluation — the draft-time verdict of check 6 never stands in
/// for it, because that check never saw a scene prompt. The past-episode score of check 10 is recomputed over the
/// series' own history, because nothing stores it.
/// </para>
/// <para>
/// <b>The free call is metered, and it is the only write outside the verdict.</b> The moderator records its
/// zero-cost <c>usage_records</c> row (<c>operation = moderation</c>, D12/D14) from
/// <see cref="Abstractions.Moderation.ModerationScope"/>, so this class pushes the scope of the evaluated episode's
/// own story generation run around the call. An autopilot run carries no reservation and no generation-run id of
/// its own, and a scope built from invented ids would write a reference that resolves to nothing, so a version
/// whose story run cannot be read is moderated un-metered (the port says so in its own log) — never with a made-up
/// run.
/// </para>
/// <para>
/// <b>Fail closed, never a pass on unknown data.</b> A missing run, a run without an episode, an episode without a
/// series, an episode without a current version and a live pass whose episode is not in <c>story_review</c> are
/// answered with an <see cref="ErrorOr{T}"/> error and no verdict at all. An unreadable corpus and an unresolvable
/// D4 band are errors too: "no pass on unknown" (plan §3.2). Every member the gate *could* read but found absent is
/// a verdict failure with its own detail code, which leaves the episode in <c>story_review</c> for the owner. The
/// two 02.8.2-T2 checks fail closed on their own unknowns too: a style template row that is gone, a content
/// document with no text and a gate that answered an error all fail check 7 with a closed detail code (never a
/// pass, and never an exception to the tick), and unreadable claims or unreadable content fail check 8.
/// </para>
/// <para>
/// <b>The true-crime hold outranks everything and approves nothing</b> (plan §3.1 checks 14–15, §8): a true-crime
/// story is held for the owner in both modes, and a <c>Hold</c> reason is recorded even in dry run, because the
/// hold — not the mode — is why the run stopped.
/// </para>
/// <para>
/// <b>One transaction per verdict.</b> The <c>gate_results</c> write, its audit row and the owner notice commit
/// together inside <see cref="GateOutcomeRecorder"/>; the approval is the command's own unit of work, so a crash
/// between the two leaves a recorded verdict and an unapproved story, never an approval nobody recorded. The replay
/// of an already-approved version is a success: an episode already approved for this same version is not approved
/// again and writes no second audit row — the only write a replay may make is the backfill of an
/// <c>autopilot.story_auto_approved</c> row the first attempt lost, and an approval the owner made is never
/// backfilled (plan §4 UC4, 02.8.2-T1 R2).
/// </para>
/// <para>
/// <b>A lost audit row is not a refusal.</b> When the approval commits but its own audit row cannot
/// (<see cref="GateErrors.ApprovalAuditFailed"/>), the verdict stays recorded as a pass and nobody is asked to
/// approve what is already approved: the error travels back to the tick, and the retry's replay is what backfills
/// the row (<see cref="GateErrors.ApprovalAuditFailedCode"/>).
/// </para>
/// <para>
/// <b>The caller pins the tenant scope.</b> The run's owner owns every row read here, and 09 README D10 makes the
/// scope — not this class — responsible for it: the caller resolves <c>ISystemContext.For(run.UserId)</c> before its
/// <c>DbContext</c> exists, exactly as the other per-user jobs do, so <see cref="IAppDbContext"/> here is already the
/// owner's. No HTTP route exposes this class; 02.8.3-T1's orchestrator is its only caller.
/// </para>
/// </remarks>
/// <param name="db">The caller's write model; every read of this class is tracked-free.</param>
/// <param name="factory">Maps the stored row, its audit report and the series into the gate's input (fail closed).</param>
/// <param name="recorder">Writes the verdict, the audit row and the owner notice in one transaction.</param>
/// <param name="approvals">Sends the user's own approve command on a live pass, as a system actor.</param>
/// <param name="contentGate">The 02.8.2-T2 content gate: check 7 over the narration, the metadata and the scene prompts.</param>
/// <param name="scenePrompts">Expands the story's keywords through the series' style template for the gate.</param>
/// <param name="realWorld">The 02.8.2-T2 detector over the current content and the review model's claims (check 8).</param>
/// <param name="options">The bound <c>Autopilot:Gates</c> section (mode, profiles).</param>
/// <param name="similarityCorpus">The past episodes of the series, scored for check 10.</param>
/// <param name="similaritySettings">The <c>Similarity</c> section: the shingle window and the scorable minimum.</param>
/// <param name="factSheets">The approved sheet of a true-crime episode (check 14).</param>
/// <param name="clock">The UTC clock: the <c>checked_at</c> of the gate's own moderation check.</param>
/// <param name="metrics">The D19 gate metrics; every return path records exactly one.</param>
/// <param name="logger">The ids-only log of this gate.</param>
public sealed partial class StoryGateEvaluator(
    IAppDbContext db,
    StoryGateInputFactory factory,
    GateOutcomeRecorder recorder,
    AutopilotApprovalService approvals,
    IAutopilotContentGate contentGate,
    ScenePromptExpander scenePrompts,
    IRealWorldSignalService realWorld,
    AutopilotGateOptions options,
    ISimilarityCorpusProvider similarityCorpus,
    SimilaritySettings similaritySettings,
    IFactSheetGuard factSheets,
    IClock clock,
    AutopilotGateMetrics metrics,
    ILogger<StoryGateEvaluator> logger)
{
    /// <summary>The <c>gate_results</c> key, the metric tag and the approval-failure row's gate name of this half.</summary>
    public const string GateName = "story";

    /// <summary>The error code of a run id that does not exist in the caller's scope.</summary>
    public const string RunMissingErrorCode = "autopilot_gate_run_missing";

    /// <summary>The error code of a run that names no episode, or of an episode that is not in the caller's scope.</summary>
    public const string EpisodeMissingErrorCode = "autopilot_gate_episode_missing";

    /// <summary>The error code of an episode whose series cannot be read.</summary>
    public const string SeriesMissingErrorCode = "autopilot_gate_series_missing";

    /// <summary>The error code of an episode with no readable current story version: there is no story to gate.</summary>
    public const string VersionMissingErrorCode = "autopilot_gate_version_missing";

    /// <summary>
    /// The error code of an episode whose state cannot carry this verdict: it is neither in <c>story_review</c> nor
    /// already approved for the version the gate judged, so no verdict is recorded and nobody is notified.
    /// </summary>
    public const string EpisodeStateErrorCode = "autopilot_gate_episode_state";

    /// <summary>The error code of a past-episode corpus that could not be read: check 10 is not decided on unknown.</summary>
    public const string CorpusUnavailableErrorCode = "autopilot_gate_corpus_unavailable";

    /// <summary>The prefix of a paragraph's D22 address (<c>p:0</c>, <c>p:7</c>), the one id with an index.</summary>
    private const string ParagraphAddressPrefix = "p:";

    /// <summary>
    /// Evaluates the story gate of one run and, on a live pass, approves the story.
    /// </summary>
    /// <param name="runId">The autopilot run whose episode's current story version is gated.</param>
    /// <param name="attempt">The story-gate attempt of the run, 1-based (D17); recorded on the verdict.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The verdict and what the gate did with it, or — never a verdict — <see cref="RunMissingErrorCode"/>,
    /// <see cref="EpisodeMissingErrorCode"/>, <see cref="SeriesMissingErrorCode"/>, <see cref="VersionMissingErrorCode"/>,
    /// <see cref="EpisodeStateErrorCode"/> or <see cref="CorpusUnavailableErrorCode"/>, the error of an unresolvable
    /// D4 band, or the error of the write/approval that failed (a refused approval travels through
    /// <see cref="GateOutcomeRecorder.RecordApprovalFailureAsync"/> instead and is answered with
    /// <see cref="GateStopReason.OwnerApprovalRequired"/>, while a committed approval whose audit row could not be
    /// written travels back unchanged as <see cref="GateErrors.ApprovalAuditFailedCode"/> so the retry backfills it).
    /// </returns>
    public async ValueTask<ErrorOr<StoryGateOutcome>> EvaluateAsync(Guid runId, int attempt, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var mode = options.Mode;

        var run = await db.AutopilotRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, ct)
            .ConfigureAwait(false);
        if (run is null)
        {
            LogRunMissing(runId);

            return Faulted(startedAt, RunMissingErrorCode, $"Autopilot run {runId} does not exist in this scope.");
        }

        if (run.EpisodeId is not { } episodeId)
        {
            LogEpisodeMissing(runId);

            return Faulted(startedAt, EpisodeMissingErrorCode, $"Autopilot run {runId} has no episode, so it has no story to gate.");
        }

        var episode = await db.Episodes
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        if (episode is null)
        {
            LogEpisodeMissing(runId);

            return Faulted(
                startedAt,
                EpisodeMissingErrorCode,
                $"The episode {episodeId} of autopilot run {runId} does not exist in this scope.");
        }

        var series = await db.Series
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, ct)
            .ConfigureAwait(false);
        if (series is null)
        {
            LogSeriesMissing(runId, episodeId, episode.SeriesId);

            return Faulted(
                startedAt,
                SeriesMissingErrorCode,
                $"The series {episode.SeriesId} of episode {episodeId} does not exist in this scope.");
        }

        if (episode.CurrentStoryVersionId is not { } versionId)
        {
            LogVersionMissing(runId, episodeId);

            return Faulted(
                startedAt,
                VersionMissingErrorCode,
                $"Episode {episodeId} of autopilot run {runId} has no current story version to gate.");
        }

        var version = await db.StoryVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == versionId, ct)
            .ConfigureAwait(false);
        if (version is null || version.EpisodeId != episodeId)
        {
            LogVersionMissing(runId, episodeId);

            return Faulted(
                startedAt,
                VersionMissingErrorCode,
                $"The current story version {versionId} of episode {episodeId} does not exist in this scope.");
        }

        var thresholdRef = StoryGateInputFactory.ReadGateThresholdsRef(series.AutopilotSettings);
        var profileName = options.ResolveName(thresholdRef);
        if (thresholdRef is not null && !string.Equals(profileName, thresholdRef, StringComparison.Ordinal))
        {
            // An unknown ref never disables the gate: it resolves to `default` and the run log shows why
            // (plan §10 decision 6). Both values are stored configuration, so the log carries a bounded,
            // control-character-free rendering of them (02.8.2-T1 SEC-6).
            LogProfileUnknown(
                runId,
                GateResultWriter.LogSafe(thresholdRef),
                GateResultWriter.LogSafe(profileName));
        }

        // SEC-1 (pinned data): a version written from a fact sheet is true crime even when the series topic was
        // re-patched to fiction, so its sheet is looked up and check 14 compares two ids.
        var effectiveTopic = StoryGateInputFactory.EffectiveTopic(series.Topic, version.FactSheetId);
        var approvedFactSheetId = await ApprovedFactSheetIdAsync(runId, episodeId, effectiveTopic, ct).ConfigureAwait(false);
        var reading = await factory
            .ReadAsync(
                new StoryGateInputFactory.StoryGateRow(
                    version.Id,
                    version.WordCount,
                    version.Keywords,
                    version.Content,
                    version.AuditReport,
                    version.SimilarityScore,
                    version.FactSheetId,
                    series.Topic,
                    series.Language,
                    episode.LengthTier,
                    series.AutopilotSettings,
                    version.CreatedAt),
                approvedFactSheetId,
                ct)
            .ConfigureAwait(false);
        if (reading.IsError)
        {
            LogUnreadable(runId, episodeId, reading.FirstError.Code);

            return WithMetric(reading.Errors, startedAt, AutopilotGateMetrics.ErrorResult, []);
        }

        var story = reading.Value;

        decimal? pastEpisodeSimilarity = null;
        var corpusMax = 0;
        if (story.StoryText is { } storyText)
        {
            try
            {
                // Check 10 (plan §3.1): the same scorer and the same normalisation the 02.5.6 guard applies to the
                // source corpus, over the series' own current bodies. A corpus that cannot be read is an error, not
                // a null score: "no pass on unknown" (plan §3.2).
                var corpus = await similarityCorpus
                    .GetPastEpisodesAsync(series.Id, episodeId, StoryGateInputFactory.PastEpisodeCorpusMax, ct)
                    .ConfigureAwait(false);
                var similarityOptions = SimilarityOptions.For(series.Language, similaritySettings);

                corpusMax = corpus.Count;
                if (corpus.Count == 0)
                {
                    // The series' first episode has no past episode to repeat: similarity 0 is a fact, not an unknown
                    // (an unreadable corpus is the exception above and never reaches here).
                    pastEpisodeSimilarity = 0m;
                }
                else if (SimilarityScorer.IsScorable(storyText, corpus, similarityOptions))
                {
                    pastEpisodeSimilarity = (decimal)Math.Round(
                        SimilarityScorer.Score(storyText, corpus, similarityOptions).Score,
                        4,
                        MidpointRounding.AwayFromZero);
                }
            }
            catch (SimilarityCorpusUnavailableException)
            {
                ct.ThrowIfCancellationRequested();
                LogCorpusUnavailable(runId, episodeId);

                return Faulted(
                    startedAt,
                    CorpusUnavailableErrorCode,
                    "The past-episode similarity corpus of the series could not be read; the story gate was not evaluated.");
            }
        }

        // 02.8.2-T2: check 7 always checks the current content itself, and check 8 detects over the same text plus
        // the claims the reviews made about it. Both are ports that cannot throw for an outage: the gate turns one
        // into a flagged verdict and the detector answers "unknown" with null, which the Domain rule fails closed.
        var profile = options.Resolve(thresholdRef);
        var moderation = await ResolveModerationAsync(run, episodeId, series, version.Id, version.Keywords, story, profile, ct)
            .ConfigureAwait(false);
        var realWorldSignal = realWorld.Detect(story.Content, series.Language, story.RealWorldClaims, profile.RealWorld);
        var input = StoryGateInputFactory.Compose(story, pastEpisodeSimilarity, moderation.Outcome, realWorldSignal, attempt);
        var result = StoryGateRules.Evaluate(input, profile);

        var replay = episode.ApprovedStoryVersionId == version.Id;

        // The episode's own state decides whether this verdict can be stored at all (02.8.2-T1 F13). The gate
        // evaluates a story under review and the replay of one already approved for this same version; every other
        // state means the run and the episode disagree. A failing verdict and a dry run are refused exactly like a
        // live pass: a verdict stored from a state the episode is not in would claim an approval that cannot
        // happen, and the owner notice it queues would ask for an action the episode no longer needs.
        if (episode.Status != EpisodeStatus.StoryReview && !replay)
        {
            LogStateNotApprovable(runId, episodeId, episode.Status, version.Id);

            return Faulted(
                startedAt,
                EpisodeStateErrorCode,
                $"Episode {episodeId} is {episode.Status} and only a story under review can be gated.");
        }

        var evidence = new GateStoryEvidence(
            SourceSimilarity: story.SourceSimilarity,
            PastEpisodeSimilarity: pastEpisodeSimilarity,
            CorpusMax: corpusMax,
            RealWorldHit: realWorldSignal?.Hit ?? false,
            RealWorldKinds: RealWorldKinds(realWorldSignal),
            RealWorldSegments: RealWorldSegments(realWorldSignal),
            ModerationFlagged: moderation.Outcome.Flagged,
            ModerationCategories: moderation.Categories,
            ModerationSegments: moderation.Segments,
            ModerationUnavailableDetail: moderation.Outcome.UnavailableDetail,
            ModerationCheckedAt: moderation.CheckedAt,
            EpisodeStatusAtEvaluation: WireName(episode.Status));

        var notifyReason = StopReasonFor(result, mode);
        var recorded = await recorder
            .RecordStoryAsync(runId, result, evidence, mode, profileName, notifyReason, ct)
            .ConfigureAwait(false);
        if (recorded.IsError)
        {
            LogRecordFailed(runId, episodeId, recorded.FirstError.Code);

            return WithMetric(recorded.Errors, startedAt, AutopilotGateMetrics.ErrorResult, result.Failures);
        }

        if (result.Hold)
        {
            // The true-crime hold (plan §3.1 checks 14–15): held by design in both modes, so the mode is not the
            // reason and nothing is approved.
            LogHeld(runId, episodeId, version.Id);

            return WithMetric(
                new StoryGateOutcome(result, false, GateStopReason.Hold),
                startedAt,
                AutopilotGateMetrics.HoldResult,
                result.Failures);
        }

        if (!result.Pass)
        {
            LogFailedChecks(runId, episodeId, version.Id, result.Failures.Count);

            return WithMetric(
                new StoryGateOutcome(result, false, notifyReason),
                startedAt,
                AutopilotGateMetrics.FailResult,
                result.Failures);
        }

        if (mode == GateMode.DryRun)
        {
            LogDryRun(runId, episodeId, version.Id);

            return WithMetric(
                new StoryGateOutcome(result, false, GateStopReason.DryRun),
                startedAt,
                AutopilotGateMetrics.DryRunResult,
                result.Failures);
        }

        // A verdict of an already-approved version is not answered here: the approval service's own replay is what
        // tells an approval the autopilot made from one the owner made, and the only write a replay may produce is
        // the backfill of an auto-approval row a lost first attempt left missing (02.8.2-T1 R2). Reading the replay
        // as a pass here would never repair that record.
        var approved = await approvals
            .ApproveStoryAsync(runId, result, evidence, mode, ct)
            .ConfigureAwait(false);
        if (approved.IsError)
        {
            if (approved.FirstError.Code == GateErrors.ApprovalAuditFailedCode)
            {
                // The approval is committed and only its audit row is missing: not a refusal, so nothing is
                // appended and nobody is asked to approve what is already approved. The error travels back to the
                // tick, whose retry is the replay that backfills the row.
                LogAuditPending(runId, episodeId, version.Id);

                return WithMetric(approved.Errors, startedAt, AutopilotGateMetrics.ErrorResult, result.Failures);
            }

            var failureCode = ApprovalFailureCodes.Of(approved.FirstError);

            LogApprovalFailed(runId, episodeId, version.Id, approved.FirstError.Code, failureCode);

            var failureRecorded = await recorder
                .RecordApprovalFailureAsync(runId, GateName, failureCode, ct)
                .ConfigureAwait(false);
            if (failureRecorded.IsError)
            {
                LogFailureNotRecorded(runId, episodeId, version.Id, failureRecorded.FirstError.Code);

                return WithMetric(failureRecorded.Errors, startedAt, AutopilotGateMetrics.ErrorResult, result.Failures);
            }

            return WithMetric(
                new StoryGateOutcome(result, false, GateStopReason.OwnerApprovalRequired),
                startedAt,
                AutopilotGateMetrics.FailResult,
                result.Failures);
        }

        if (approved.Value == ApprovalOutcome.Approved)
        {
            LogApproved(runId, episodeId, version.Id);

            return WithMetric(
                new StoryGateOutcome(result, true, null),
                startedAt,
                AutopilotGateMetrics.PassResult,
                result.Failures);
        }

        if (approved.Value == ApprovalOutcome.AlreadyApproved)
        {
            // A replay of an approval already on record: no command was sent again, and the only write it may
            // have made is the backfill of an auto-approval row a lost first attempt left missing — never a row
            // for the owner's own approval (plan §4 UC4).
            LogReplay(runId, episodeId, version.Id);

            return WithMetric(
                new StoryGateOutcome(result, true, null),
                startedAt,
                AutopilotGateMetrics.PassResult,
                result.Failures);
        }

        // A live pass cannot come back skipped; reading it as "nothing was approved" is the safe direction.
        LogNotApproved(runId, episodeId, version.Id);

        return WithMetric(
            new StoryGateOutcome(result, false, GateStopReason.DryRun),
            startedAt,
            AutopilotGateMetrics.DryRunResult,
            result.Failures);
    }

    // Check 14's other operand: the sheet the series' owner approved. Only true crime has one (the other checks
    // never read it), and a guard refusal is a null on the version's side too — the check then fails closed and the
    // owner is asked, which is the plan's direction for an unreadable approval prerequisite.
    private async ValueTask<Guid?> ApprovedFactSheetIdAsync(
        Guid runId,
        Guid episodeId,
        Topic topic,
        CancellationToken ct)
    {
        if (topic != Topic.TrueCrime)
        {
            return null;
        }

        var approved = await factSheets.EnsureApprovedAsync(episodeId, topic, ct).ConfigureAwait(false);
        if (approved.IsError)
        {
            LogFactSheetUnavailable(runId, episodeId, approved.FirstError.Code);

            return null;
        }

        return approved.Value.FactSheetId;
    }

    // Check 7 (02.8.2-T2, plan §3.1): the content gate over the current narration, the metadata and the expanded
    // scene prompts, with the profile's per-category thresholds. The draft-time verdict of check 6 is never reused
    // here: it never saw a scene prompt, and T2's gate is the one verdict the run records (plan §3.3). Every way
    // this can fail answers a flagged outcome with a closed detail code — a missing content document, a style
    // template row that is gone, and an error from the gate itself are all "the owner must look", never a pass and
    // never an exception to the tick.
    private async ValueTask<ResolvedModeration> ResolveModerationAsync(
        AutopilotRun run,
        Guid episodeId,
        NightTales.Domain.Series.Series series,
        Guid versionId,
        IReadOnlyList<string> keywords,
        StoryGateInputFactory.StoryGateReading story,
        AutopilotGateProfile profile,
        CancellationToken ct)
    {
        if (story.Content is not { } content)
        {
            LogModerationContentMissing(run.Id, episodeId);

            return new ResolvedModeration(ContentGateOutcomes.FailClosed(GateDetailCodes.FieldAbsent), [], [], clock.UtcNow);
        }

        var style = await db.StyleTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(template => template.Id == series.StyleTemplateId, ct)
            .ConfigureAwait(false);
        if (style is null)
        {
            // 03.1.1-T1's rows are seeded platform data: a series that names one which is gone cannot have its
            // scene prompts expanded, so there is nothing honest to moderate (plan §"Failure handling").
            LogStyleTemplateMissing(run.Id, episodeId, series.StyleTemplateId);

            return new ResolvedModeration(ContentGateOutcomes.FailClosed(GateDetailCodes.FieldAbsent), [], [], clock.UtcNow);
        }

        var request = new AutopilotContentGateRequest(
            run.UserId,
            episodeId,
            versionId,
            StoryTextSegments.From(content),
            scenePrompts.Expand(style, keywords),
            profile);

        var metering = await MeteringContextAsync(run, episodeId, versionId, story.PriceVersion, ct).ConfigureAwait(false);
        if (metering is null)
        {
            LogUnmeteredModeration(run.Id, episodeId);
        }

        using (metering is null ? null : ModerationScope.Push(metering))
        {
            var verdict = await contentGate.CheckAsync(request, ct).ConfigureAwait(false);
            if (verdict.IsError)
            {
                LogContentGateFailed(run.Id, episodeId, verdict.FirstError.Code);

                return new ResolvedModeration(ContentGateOutcomes.FailClosed(verdict.FirstError), [], [], clock.UtcNow);
            }

            var value = verdict.Value;

            return new ResolvedModeration(
                ContentGateOutcomes.From(value),
                value.Flagged ? value.Categories : [],
                value.FlaggedSegments,
                clock.UtcNow);
        }
    }

    // The scope of the free call. The moderator writes its zero-cost usage row from the ambient run (D12/D14), and an
    // autopilot run carries neither a reservation nor a generation-run id; the scope is therefore the generation run
    // that wrote the evaluated version (else the episode's newest story run), chosen so every reference the row
    // stores - reservation, generation run, owner - resolves to a real row, because `usage_records.generation_run_id`
    // is a foreign key. A run that cannot be found, or one that carries no reservation, leaves the check unmetered:
    // it still runs, and only its bookkeeping row is missing (CR-1 applies to the reverse direction).
    private async ValueTask<RunContext?> MeteringContextAsync(
        AutopilotRun run,
        Guid episodeId,
        Guid versionId,
        string priceVersion,
        CancellationToken ct)
    {
        var generationRun = await db.GenerationRuns
            .AsNoTracking()
            .Where(candidate => candidate.EpisodeId == episodeId && candidate.Kind == GenerationKind.Story)
            .OrderBy(candidate => candidate.ResultStoryVersionId == versionId ? 0 : 1)
            .ThenByDescending(candidate => candidate.QueuedAt)
            .Select(candidate => new { candidate.Id, candidate.Trigger, candidate.Attempt, candidate.ReservationId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (generationRun?.ReservationId is not { } reservation)
        {
            return null;
        }

        return new RunContext(
            generationRun.Id,
            run.UserId,
            run.SeriesId,
            episodeId,
            new ReservationId(reservation),
            generationRun.Trigger,
            Math.Max(1, generationRun.Attempt),
            priceVersion);
    }

    /// <summary>
    /// The reason the owner is told about, or <see langword="null"/> for a live pass that approved (plan §4 UC6). The
    /// true-crime hold outranks both the mode and a failing check; the failing moderation check's own detail decides
    /// between "the moderator could not be reached" and "checks failed".
    /// </summary>
    /// <param name="result">The verdict.</param>
    /// <param name="mode">The mode the gate ran in.</param>
    /// <returns>The reason, or null when the owner is not notified.</returns>
    private static GateStopReason? StopReasonFor(StoryGateResult result, GateMode mode)
    {
        if (result.Hold)
        {
            return GateStopReason.Hold;
        }

        if (!result.Pass)
        {
            return ModeratorUnavailable(result) ? GateStopReason.ModeratorUnavailable : GateStopReason.FailedChecks;
        }

        return mode == GateMode.DryRun ? GateStopReason.DryRun : null;
    }

    // A moderation check that could not complete is the reason the owner has to act, not a generic failed check
    // (plan §3.1 check 7, §4 UC6).
    private static bool ModeratorUnavailable(StoryGateResult result) =>
        result.Checks.Any(check => !check.Pass
            && string.Equals(check.Id, StoryGateCheckIds.ModerationGate, StringComparison.Ordinal)
            && string.Equals(check.Detail, GateDetailCodes.ModeratorUnavailable, StringComparison.Ordinal));

    // The kinds that fired, once each: the payload carries kinds and ids, never the matched phrase or the excerpt
    // (D19/D22, 02.8.2-T2 plan §3.4).
    private static IReadOnlyList<string> RealWorldKinds(RealWorldSignal? signal) =>
        signal is null
            ? []
            : [.. signal.Hits.Select(hit => WireName(hit.Kind)).Distinct(StringComparer.Ordinal)];

    // Where the signals fired, once each and in hit order: the D22 ids the owner jumps to in the editor (10.5.10).
    // A model-claim hit carries the address the stored review wrote — only its length was capped there, and it was
    // validated against the text of an older version — so an address that does not have the D22 shape names nothing
    // to jump to and is not listed; the hit itself is still counted in RealWorldKinds, so the verdict never loses it.
    private static IReadOnlyList<string> RealWorldSegments(RealWorldSignal? signal) =>
        signal is null
            ? []
            : [.. signal.Hits
                .Select(hit => hit.Segment)
                .Where(IsSegmentAddress)
                .Distinct(StringComparer.Ordinal)];

    // The D22 shape: the four fixed ids, or `p:<digits>`. Digits only — NumberStyles.None refuses a sign, a decimal
    // separator, a stray letter and surrounding whitespace (the review path's own rule, ReviewResultValidator), so a
    // claim's prose never becomes an id `gate_results` lists. Only the shape is checked: an index this version no
    // longer has (`p:7` after a revision) still has the shape and stays listed, because the editor addresses what the
    // review wrote against.
    private static bool IsSegmentAddress(string segment) =>
        segment switch
        {
            SegmentAddress.Title or SegmentAddress.Hook or SegmentAddress.Ending or SegmentAddress.Meta => true,
            _ => segment.StartsWith(ParagraphAddressPrefix, StringComparison.Ordinal)
                && int.TryParse(
                    segment.AsSpan(ParagraphAddressPrefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _),
        };

    /// <summary>The wire name <c>gate_results</c> stores for an enum member: its snake_case form (plan §3.4).</summary>
    /// <param name="value">The enum member.</param>
    /// <returns>The stored name.</returns>
    private static string WireName<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Records the one metric of this call and returns <paramref name="outcome"/>.</summary>
    /// <param name="outcome">What the caller gets.</param>
    /// <param name="startedAt">The <see cref="Stopwatch"/> timestamp the evaluation started at.</param>
    /// <param name="metricResult">One of the <see cref="AutopilotGateMetrics"/> result names.</param>
    /// <param name="failedCheckIds">The verdict's failures, in verdict order.</param>
    /// <returns><paramref name="outcome"/>.</returns>
    private ErrorOr<StoryGateOutcome> WithMetric(
        ErrorOr<StoryGateOutcome> outcome,
        long startedAt,
        string metricResult,
        IReadOnlyList<string> failedCheckIds)
    {
        metrics.RecordGate(GateName, metricResult, Stopwatch.GetElapsedTime(startedAt), failedCheckIds);

        return outcome;
    }

    /// <summary>Records the one metric of an evaluation that has no verdict, and returns the error.</summary>
    /// <param name="startedAt">The <see cref="Stopwatch"/> timestamp the evaluation started at.</param>
    /// <param name="code">The error code.</param>
    /// <param name="description">The error description; ids and codes only.</param>
    /// <returns>The error, as an <see cref="ErrorOr{T}"/>.</returns>
    private ErrorOr<StoryGateOutcome> Faulted(long startedAt, string code, string description) =>
        WithMetric(Error.Failure(code, description), startedAt, AutopilotGateMetrics.ErrorResult, []);

    /// <summary>One resolved check-7 verdict, with what <c>gate_results</c> records about it.</summary>
    /// <param name="Outcome">The verdict the Domain rule reads.</param>
    /// <param name="Categories">The flagged categories; empty when nothing was flagged.</param>
    /// <param name="Segments">The D22 ids the flag was attributed to, plus <c>scene:&lt;i&gt;</c> for a scene prompt; ids only.</param>
    /// <param name="CheckedAt">When the verdict was produced.</param>
    private sealed record ResolvedModeration(
        ModerationGateOutcome Outcome,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> Segments,
        DateTimeOffset? CheckedAt);

    [LoggerMessage(
        EventId = 6460,
        Level = LogLevel.Information,
        Message = "The story gate of autopilot run {RunId} approved story version {StoryVersionId} of episode {EpisodeId} as a system actor.")]
    private partial void LogApproved(Guid runId, Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6461,
        Level = LogLevel.Information,
        Message = "The story gate of autopilot run {RunId} passed story version {StoryVersionId} of episode {EpisodeId} in dry-run mode; nothing was approved.")]
    private partial void LogDryRun(Guid runId, Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6462,
        Level = LogLevel.Information,
        Message = "The story gate of autopilot run {RunId} replayed the approval of story version {StoryVersionId} of episode {EpisodeId}; no second approval was sent.")]
    private partial void LogReplay(Guid runId, Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6463,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} failed {FailureCount} checks of story version {StoryVersionId} of episode {EpisodeId}; the story stays in review.")]
    private partial void LogFailedChecks(Guid runId, Guid episodeId, Guid storyVersionId, int failureCount);

    [LoggerMessage(
        EventId = 6464,
        Level = LogLevel.Information,
        Message = "The story gate of autopilot run {RunId} held story version {StoryVersionId} of episode {EpisodeId} for the owner: the true-crime hold never auto-approves.")]
    private partial void LogHeld(Guid runId, Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6465,
        Level = LogLevel.Warning,
        Message = "The approval of story version {StoryVersionId} of episode {EpisodeId} for autopilot run {RunId} was refused with {ErrorCode}; recorded as {FailureCode}.")]
    private partial void LogApprovalFailed(Guid runId, Guid episodeId, Guid storyVersionId, string errorCode, string failureCode);

    [LoggerMessage(
        EventId = 6466,
        Level = LogLevel.Error,
        Message = "The approval failure of story version {StoryVersionId} of episode {EpisodeId} for autopilot run {RunId} could not be recorded ({ErrorCode}); the owner was not notified by this call.")]
    private partial void LogFailureNotRecorded(Guid runId, Guid episodeId, Guid storyVersionId, string errorCode);

    [LoggerMessage(
        EventId = 6467,
        Level = LogLevel.Error,
        Message = "The story verdict of autopilot run {RunId} for episode {EpisodeId} could not be recorded ({ErrorCode}).")]
    private partial void LogRecordFailed(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6468,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} was not evaluated: the run does not exist in this scope.")]
    private partial void LogRunMissing(Guid runId);

    [LoggerMessage(
        EventId = 6469,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} was not evaluated: its episode is missing.")]
    private partial void LogEpisodeMissing(Guid runId);

    [LoggerMessage(
        EventId = 6470,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} was not evaluated: series {SeriesId} of episode {EpisodeId} does not exist in this scope.")]
    private partial void LogSeriesMissing(Guid runId, Guid episodeId, Guid seriesId);

    [LoggerMessage(
        EventId = 6471,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} was not evaluated: episode {EpisodeId} has no readable current story version.")]
    private partial void LogVersionMissing(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6472,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} found episode {EpisodeId} in {Status}; story version {StoryVersionId} cannot be approved from there.")]
    private partial void LogStateNotApprovable(Guid runId, Guid episodeId, EpisodeStatus status, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6473,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} names unknown gate profile {ProfileRef}; the gate resolves {ProfileName} instead.")]
    private partial void LogProfileUnknown(Guid runId, string profileRef, string profileName);

    [LoggerMessage(
        EventId = 6474,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} was not evaluated: the past-episode corpus of episode {EpisodeId} could not be read.")]
    private partial void LogCorpusUnavailable(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6475,
        Level = LogLevel.Warning,
        Message = "The content gate of autopilot run {RunId} could not check episode {EpisodeId} ({ErrorCode}); check 7 fails closed and the owner is asked.")]
    private partial void LogContentGateFailed(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6476,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} could not read the approved fact sheet of episode {EpisodeId} ({ErrorCode}); check 14 fails closed.")]
    private partial void LogFactSheetUnavailable(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6477,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} could not read the content document of episode {EpisodeId}; check 7 and check 8 fail closed.")]
    private partial void LogModerationContentMissing(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6478,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} could not read its input for episode {EpisodeId} ({ErrorCode}); no verdict was recorded.")]
    private partial void LogUnreadable(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6526,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} cannot expand the scene prompts of episode {EpisodeId}: style template {StyleTemplateId} does not exist; check 7 fails closed.")]
    private partial void LogStyleTemplateMissing(Guid runId, Guid episodeId, Guid styleTemplateId);

    [LoggerMessage(
        EventId = 6527,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} moderates episode {EpisodeId} outside a run scope; the free call is not metered.")]
    private partial void LogUnmeteredModeration(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6479,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} passed story version {StoryVersionId} of episode {EpisodeId} but nothing was approved.")]
    private partial void LogNotApproved(Guid runId, Guid episodeId, Guid storyVersionId);

    [LoggerMessage(
        EventId = 6438,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} approved story version {StoryVersionId} of episode {EpisodeId}, but its audit row could not be written; the retry backfills it.")]
    private partial void LogAuditPending(Guid runId, Guid episodeId, Guid storyVersionId);
}
