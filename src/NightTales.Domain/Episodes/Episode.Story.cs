using ErrorOr;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Generation.Events;

namespace NightTales.Domain.Episodes;

/// <summary>
/// The story-generation part of the episode state machine (02.2.2 plan §3.3, transitions T1–T9). The episode
/// only needs to know <em>that</em> it is generating: <c>status</c> plus <c>generation_runs.previous_episode_status</c>
/// is the whole state, so no <c>episodes</c> column was added. The run's own lifecycle lives on
/// <see cref="GenerationRun"/>; the Application service <c>StoryGenerationProgress</c> calls both.
/// </summary>
/// <remarks>
/// <para>
/// Every method is idempotent for a repeat with the same run and the same target (the at-least-once outbox of
/// 09.2.4 can deliver a step twice): the second call returns <c>Success</c> and raises no second event. Two
/// repeats are recognised from the persisted state alone (<c>Complete…</c> by the current version, and the status
/// itself); the others are remembered on the instance, which covers one aggregate's lifetime — across
/// transactions the run's terminal status (checked by the job before it calls the episode) is the authority.
/// </para>
/// <para>
/// The sketch in the task file lists no trigger on four of the methods, but every D21 event payload carries one
/// and the episode stores neither the run nor its trigger, so the caller passes both — the values come from the
/// <see cref="GenerationRun"/> row it already loaded.
/// </para>
/// </remarks>
public sealed partial class Episode
{
    /// <summary>
    /// The code of "the episode is not <c>story_generating</c>" (T9): a sweeper or an admin already moved it, so
    /// the caller logs and returns without touching the episode. It is a domain signal, not an HTTP code.
    /// </summary>
    public const string NotGeneratingCode = "episode_not_generating";

    private Guid? _requestedRunId;
    private (Guid RunId, GenerationStage Stage)? _lastStage;
    private (Guid RunId, bool Cancelled)? _endedRun;

    /// <summary>
    /// Starts a story generation (T1 from <c>draft</c>, T2 from <c>story_review</c>): the episode becomes
    /// <c>story_generating</c> and <see cref="StoryGenerationRequested"/> is raised. A review-state episode keeps its
    /// current version until the new one is saved. The true-crime fact-sheet check is the handler's, not the domain's.
    /// </summary>
    /// <param name="generationRunId">The <c>generation_runs</c> row created in the same transaction.</param>
    /// <param name="trigger">Who asked.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a repeat for the same run is also <c>Success</c> without a second event. Any other state
    /// (T3, T4) is 409 <see cref="InvalidStateCode"/>.
    /// </returns>
    public ErrorOr<Success> RequestStoryGeneration(Guid generationRunId, GenerationTrigger trigger, DateTimeOffset now)
    {
        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (Status == EpisodeStatus.StoryGenerating)
        {
            return _requestedRunId == generationRunId ? Result.Success : InvalidState();
        }

        if (Status is not (EpisodeStatus.Draft or EpisodeStatus.StoryReview))
        {
            return InvalidState();
        }

        Status = EpisodeStatus.StoryGenerating;
        UpdatedAt = now;
        _requestedRunId = generationRunId;
        _lastStage = null;
        _endedRun = null;

        Raise(new StoryGenerationRequested(Id, SeriesId, UserId, generationRunId, trigger, now));

        return Result.Success;
    }

    /// <summary>
    /// Reports that the run entered <paramref name="stage"/> (T5): the status stays <c>story_generating</c> and
    /// <see cref="StoryGenerationStageChanged"/> is raised. The stage order itself is enforced by
    /// <see cref="GenerationRun.AdvanceStage"/>; a repeat of the same stage raises nothing.
    /// </summary>
    /// <param name="generationRunId">The run that changed stage.</param>
    /// <param name="stage">The stage it is in now.</param>
    /// <param name="trigger">Who asked for the run.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <returns><c>Success</c>, or <see cref="NotGeneratingCode"/> when the episode is not generating (T9).</returns>
    public ErrorOr<Success> MarkGenerationStage(
        Guid generationRunId,
        GenerationStage stage,
        GenerationTrigger trigger,
        DateTimeOffset now)
    {
        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (Status != EpisodeStatus.StoryGenerating)
        {
            return NotGenerating();
        }

        if (_lastStage is { } last && last.RunId == generationRunId && last.Stage == stage)
        {
            return Result.Success;
        }

        _lastStage = (generationRunId, stage);

        Raise(new StoryGenerationStageChanged(Id, SeriesId, UserId, generationRunId, stage, trigger, now));

        return Result.Success;
    }

    /// <summary>
    /// Finishes the generation (T6): the episode becomes <c>story_review</c> with
    /// <paramref name="currentStoryVersionId"/> as its current version and <see cref="StoryGenerated"/> is raised
    /// (<c>Revised</c> when more than one version was saved). A repeat with the same current version is
    /// <c>Success</c> without a second event.
    /// </summary>
    /// <param name="generationRunId">The run that saved the versions.</param>
    /// <param name="trigger">Who asked for the run.</param>
    /// <param name="versionIds">Every version the run saved; non-empty and containing <paramref name="currentStoryVersionId"/>.</param>
    /// <param name="currentStoryVersionId">The version that becomes the episode's current one.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a validation error when the version list is empty or lacks the current version;
    /// <see cref="NotGeneratingCode"/> when the episode is not generating (T9).
    /// </returns>
    public ErrorOr<Success> CompleteStoryGeneration(
        Guid generationRunId,
        GenerationTrigger trigger,
        IReadOnlyList<Guid> versionIds,
        Guid currentStoryVersionId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(versionIds);

        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (Status == EpisodeStatus.StoryReview && CurrentStoryVersionId == currentStoryVersionId)
        {
            return Result.Success;
        }

        if (Status != EpisodeStatus.StoryGenerating)
        {
            return NotGenerating();
        }

        if (versionIds.Count == 0)
        {
            return Error.Validation("story_versions_required", "A completed generation saved at least one version.");
        }

        if (!versionIds.Contains(currentStoryVersionId))
        {
            return Error.Validation(
                "story_current_version_unknown",
                "The current version must be one of the versions the generation saved.");
        }

        Status = EpisodeStatus.StoryReview;
        CurrentStoryVersionId = currentStoryVersionId;
        UpdatedAt = now;

        Raise(new StoryGenerated(
            Id,
            SeriesId,
            UserId,
            generationRunId,
            currentStoryVersionId,
            Revised: versionIds.Count > 1,
            trigger,
            now));

        return Result.Success;
    }

    /// <summary>
    /// Ends a failed generation (T7, F04): the episode returns to <paramref name="previousStatus"/> — the one the
    /// run stored, <c>draft</c> or <c>story_review</c>, never blindly <c>story_review</c> — and
    /// <see cref="StoryGenerationFailed"/> is raised. The error code and the counting flag are written to the
    /// <see cref="GenerationRun"/> by the caller (<see cref="GenerationRun.Fail"/>); the episode does not store them.
    /// </summary>
    /// <param name="generationRunId">The run that failed.</param>
    /// <param name="trigger">Who asked for the run.</param>
    /// <param name="previousStatus">The status the run stored before the episode went generating.</param>
    /// <param name="code">The D8 code the run ended with.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <returns>
    /// <c>Success</c> (a repeat for the same run too); a validation error when <paramref name="previousStatus"/> is
    /// not a valid return target; <see cref="NotGeneratingCode"/> when the episode is not generating (T9).
    /// </returns>
    public ErrorOr<Success> FailStoryGeneration(
        Guid generationRunId,
        GenerationTrigger trigger,
        EpisodeStatus previousStatus,
        GenerationErrorCode code,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(code);

        return EndStoryGeneration(generationRunId, trigger, previousStatus, code, cancelled: false, now);
    }

    /// <summary>
    /// Cancels a generation (T8): like <see cref="FailStoryGeneration"/> with
    /// <see cref="GenerationErrorCode.StoryGenerationCancelled"/> (D8, TL T20), and the same return target.
    /// </summary>
    /// <param name="generationRunId">The run that was cancelled.</param>
    /// <param name="trigger">Who asked for the run.</param>
    /// <param name="previousStatus">The status the run stored before the episode went generating.</param>
    /// <param name="now">The caller's clock reading.</param>
    /// <returns>As <see cref="FailStoryGeneration"/>.</returns>
    public ErrorOr<Success> CancelStoryGeneration(
        Guid generationRunId,
        GenerationTrigger trigger,
        EpisodeStatus previousStatus,
        DateTimeOffset now) =>
        EndStoryGeneration(
            generationRunId,
            trigger,
            previousStatus,
            GenerationErrorCode.StoryGenerationCancelled,
            cancelled: true,
            now);

    private ErrorOr<Success> EndStoryGeneration(
        Guid generationRunId,
        GenerationTrigger trigger,
        EpisodeStatus previousStatus,
        GenerationErrorCode code,
        bool cancelled,
        DateTimeOffset now)
    {
        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (_endedRun is { } ended && ended.RunId == generationRunId && ended.Cancelled == cancelled
            && Status == previousStatus)
        {
            return Result.Success;
        }

        if (Status != EpisodeStatus.StoryGenerating)
        {
            return NotGenerating();
        }

        if (previousStatus is not (EpisodeStatus.Draft or EpisodeStatus.StoryReview))
        {
            return Error.Validation(
                "story_previous_status_invalid",
                "A failed generation returns the episode to draft or story_review.");
        }

        Status = previousStatus;
        UpdatedAt = now;
        _endedRun = (generationRunId, cancelled);

        Raise(new StoryGenerationFailed(Id, UserId, generationRunId, code, trigger, now));

        return Result.Success;
    }

    private static Error InvalidState() =>
        Error.Conflict(InvalidStateCode, "A story cannot be generated in the episode's current state.");

    private static Error NotGenerating() =>
        Error.Conflict(NotGeneratingCode, "The episode is not generating a story.");

    private static Error RunIdRequired() =>
        Error.Validation("generation_run_id_required", "A generation run id is required.");
}
