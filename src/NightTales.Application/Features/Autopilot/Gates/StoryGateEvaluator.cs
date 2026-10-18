using System.Diagnostics;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Similarity;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Gates;
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
/// <b>Reads only, and never a paid call.</b> The audit report, the stored content, the stored similarity score and
/// the series' approved fact sheet are all that the checks read; the one outbound call is the free
/// <c>autopilot_gate</c> moderation check, and only when the draft-time verdict is missing, stale or unavailable
/// (02.8.2-T1 decision 1). The past-episode score of check 10 is recomputed over the series' own history, because
/// nothing stores it.
/// </para>
/// <para>
/// <b>Fail closed, never a pass on unknown data.</b> A missing run, a run without an episode, an episode without a
/// series, an episode without a current version and a live pass whose episode is not in <c>story_review</c> are
/// answered with an <see cref="ErrorOr{T}"/> error and no verdict at all. An unreadable corpus and an unresolvable
/// D4 band are errors too: "no pass on unknown" (plan §3.2). Every member the gate *could* read but found absent is
/// a verdict failure with its own detail code, which leaves the episode in <c>story_review</c> for the owner.
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
/// again and writes no second audit row.
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
/// <param name="moderator">The free <c>autopilot_gate</c> moderation check of check 7, used only when the stored verdict is unusable.</param>
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
    IContentModerator moderator,
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
    /// The error code of a live pass whose episode is neither in <c>story_review</c> nor already approved for this
    /// version: the approve command could not succeed, so no verdict is recorded.
    /// </summary>
    public const string EpisodeStateErrorCode = "autopilot_gate_episode_state";

    /// <summary>The error code of a past-episode corpus that could not be read: check 10 is not decided on unknown.</summary>
    public const string CorpusUnavailableErrorCode = "autopilot_gate_corpus_unavailable";

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
    /// <see cref="GateStopReason.OwnerApprovalRequired"/>).
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
            // (plan §10 decision 6).
            LogProfileUnknown(runId, thresholdRef, profileName);
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

        var moderation = await ResolveModerationAsync(runId, episodeId, story, ct).ConfigureAwait(false);
        var input = StoryGateInputFactory.Compose(story, pastEpisodeSimilarity, moderation.Outcome, attempt);
        var result = StoryGateRules.Evaluate(input, options.Resolve(thresholdRef));

        var replay = episode.ApprovedStoryVersionId == version.Id;

        // The approve path needs the state the command itself requires: only `story_review` accepts it. An episode
        // already approved for this same version is this gate's own approval replayed; every other state means the
        // run and the episode disagree — the gate records no verdict there, because a stored pass would claim an
        // approval that cannot happen.
        if (result.Pass && mode == GateMode.Live && episode.Status != EpisodeStatus.StoryReview && !replay)
        {
            LogStateNotApprovable(runId, episodeId, episode.Status, version.Id);

            return Faulted(
                startedAt,
                EpisodeStateErrorCode,
                $"Episode {episodeId} is {episode.Status} and only a story under review can be approved.");
        }

        var evidence = new GateStoryEvidence(
            SourceSimilarity: story.SourceSimilarity,
            PastEpisodeSimilarity: pastEpisodeSimilarity,
            CorpusMax: corpusMax,
            RealWorldHit: story.RealWorld?.Hit ?? false,
            RealWorldKinds: RealWorldKinds(story.RealWorld),
            ModerationFlagged: moderation.Outcome.Flagged,
            ModerationCategories: moderation.Categories,
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

        if (replay)
        {
            LogReplay(runId, episodeId, version.Id);

            return WithMetric(
                new StoryGateOutcome(result, true, null),
                startedAt,
                AutopilotGateMetrics.PassResult,
                result.Failures);
        }

        var approved = await approvals
            .ApproveStoryAsync(runId, result, evidence, mode, ct)
            .ConfigureAwait(false);
        if (approved.IsError)
        {
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
            // The command's own replay: the approval — and its audit row — belong to the first call, so this one
            // adds neither (plan §4 UC4).
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

    // Check 7 (02.8.2-T1 decision 1): the draft-time verdict when the stored block is a complete, current one;
    // otherwise the free moderation check over the narration and the metadata, exactly as D12 scopes the stage. A
    // moderator that cannot be reached flags the check and carries `moderator_unavailable`, so it never passes on an
    // unknown (plan §7), and an unreadable draft (no text at all) is flagged the same way rather than checked clean
    // over nothing.
    private async ValueTask<ResolvedModeration> ResolveModerationAsync(
        Guid runId,
        Guid episodeId,
        StoryGateInputFactory.StoryGateReading story,
        CancellationToken ct)
    {
        if (story.Moderation.Usable)
        {
            return new ResolvedModeration(
                new ModerationGateOutcome(story.Moderation.Flagged, null),
                story.Moderation.Categories,
                story.Moderation.CheckedAt);
        }

        if (story.ModerationTexts.Count == 0)
        {
            LogModerationTextMissing(runId, episodeId);

            return new ResolvedModeration(
                new ModerationGateOutcome(true, GateDetailCodes.FieldAbsent),
                [],
                clock.UtcNow);
        }

        try
        {
            var result = await moderator
                .CheckAsync(story.ModerationTexts, ModerationStage.AutopilotGate, ct)
                .ConfigureAwait(false);

            return new ResolvedModeration(
                new ModerationGateOutcome(result.Flagged, null),
                result.Categories,
                clock.UtcNow);
        }
        catch (ModerationUnavailableException)
        {
            ct.ThrowIfCancellationRequested();
            LogModeratorUnavailable(runId, episodeId);

            return new ResolvedModeration(
                new ModerationGateOutcome(true, GateDetailCodes.ModeratorUnavailable),
                [],
                clock.UtcNow);
        }
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

    // The kinds of the persisted model claims, once each: the payload carries ids and kinds, never the claim's own
    // wording (02.8.2-T1 decision 1, D19/D22).
    private static IReadOnlyList<string> RealWorldKinds(RealWorldSignal? signal) =>
        signal is null
            ? []
            : [.. signal.Hits.Select(hit => WireName(hit.Kind)).Distinct(StringComparer.Ordinal)];

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
    /// <param name="Categories">The flagged categories (stored or fresh); empty when nothing was flagged.</param>
    /// <param name="CheckedAt">When the verdict was produced, or null when the stored block did not record it.</param>
    private sealed record ResolvedModeration(
        ModerationGateOutcome Outcome,
        IReadOnlyList<string> Categories,
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
        Message = "The story gate of autopilot run {RunId} could not use the moderator for episode {EpisodeId}; the moderation check fails closed and the owner is asked.")]
    private partial void LogModeratorUnavailable(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6476,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} could not read the approved fact sheet of episode {EpisodeId} ({ErrorCode}); check 14 fails closed.")]
    private partial void LogFactSheetUnavailable(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6477,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} could not read the stored moderation verdict of episode {EpisodeId}; the free check runs instead.")]
    private partial void LogModerationTextMissing(Guid runId, Guid episodeId);

    [LoggerMessage(
        EventId = 6478,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} could not read its input for episode {EpisodeId} ({ErrorCode}); no verdict was recorded.")]
    private partial void LogUnreadable(Guid runId, Guid episodeId, string errorCode);

    [LoggerMessage(
        EventId = 6479,
        Level = LogLevel.Warning,
        Message = "The story gate of autopilot run {RunId} passed story version {StoryVersionId} of episode {EpisodeId} but nothing was approved.")]
    private partial void LogNotApproved(Guid runId, Guid episodeId, Guid storyVersionId);
}
