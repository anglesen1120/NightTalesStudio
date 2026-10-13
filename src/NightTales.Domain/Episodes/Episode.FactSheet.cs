using ErrorOr;
using NightTales.Domain.Research.Events;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Domain.Episodes;

/// <summary>
/// The fact-sheet part of the episode state machine (02.5.3 plan §4.1/§5). A fact-sheet run never changes
/// <see cref="Episode.Status"/>: <see cref="EpisodeStatus"/> has no fact-sheet value, only a <c>story</c> run
/// moves an episode, and the run itself plus D2's "at most one active run per (episode, kind)" partial unique
/// index are the whole state. These methods therefore only guard the precondition (the episode may carry a fact
/// sheet: <c>draft</c> or <c>story_review</c>) and raise the events the outbox turns into the UI's
/// <c>EpisodeUpdated</c>.
/// </summary>
/// <remarks>
/// The fact sheet itself is a separate aggregate (<c>NightTales.Domain.Research.FactSheet</c>): the job inserts
/// it and this aggregate announces it, exactly as a fetched source is announced by
/// <see cref="RaiseSourceFetched"/>. A repeat with the same run is idempotent within one aggregate's lifetime;
/// across transactions the run's terminal status, checked by the job before it calls this aggregate, is the
/// authority.
/// </remarks>
public sealed partial class Episode
{
    private Guid? _requestedFactSheetRunId;

    /// <summary>
    /// Starts a fact-sheet generation (02.5.3 plan §4.1): the episode must be <c>draft</c> or
    /// <c>story_review</c> and <see cref="FactSheetGenerationRequested"/> is raised. The status does not change.
    /// </summary>
    /// <param name="generationRunId">The <c>generation_runs</c> row created in the same transaction.</param>
    /// <param name="trigger">Who asked.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a repeat for the same run is also <c>Success</c> without a second event. Any other state
    /// is 409 <see cref="InvalidStateCode"/>.
    /// </returns>
    public ErrorOr<Success> RequestFactSheetGeneration(Guid generationRunId, GenerationTrigger trigger, DateTimeOffset now)
    {
        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (_requestedFactSheetRunId == generationRunId)
        {
            return Result.Success;
        }

        if (!CanEditStory)
        {
            return Error.Conflict(InvalidStateCode, "A fact sheet cannot be built in the episode's current state.");
        }

        _requestedFactSheetRunId = generationRunId;

        Raise(new FactSheetGenerationRequested(Id, SeriesId, UserId, generationRunId, trigger, now));

        return Result.Success;
    }

    /// <summary>
    /// Announces the draft version a fact-sheet run saved (02.5.3 plan §5): <see cref="FactSheetGenerated"/> is
    /// raised for the UI's <c>EpisodeUpdated</c>. The status does not change.
    /// </summary>
    /// <remarks>
    /// No state is required: the version is already stored and a story generation that started while the run was
    /// finishing must not turn a saved sheet into a failed run. The event is an invalidation, nothing else.
    /// </remarks>
    /// <param name="generationRunId">The run that saved the version.</param>
    /// <param name="factSheetId">The saved version.</param>
    /// <param name="version">The saved version number.</param>
    /// <param name="trigger">Who asked for the run.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a validation error when a required id is empty or <paramref name="version"/> is below 1.
    /// </returns>
    public ErrorOr<Success> RaiseFactSheetGenerated(
        Guid generationRunId,
        Guid factSheetId,
        int version,
        GenerationTrigger trigger,
        DateTimeOffset now)
    {
        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (factSheetId == Guid.Empty)
        {
            return Error.Validation("fact_sheet_id_required", "A saved fact sheet needs its id.");
        }

        if (version < 1)
        {
            return Error.Validation("fact_sheet_version_invalid", "A fact-sheet version is 1-based.");
        }

        Raise(new FactSheetGenerated(factSheetId, version, Id, SeriesId, UserId, generationRunId, trigger, now));

        return Result.Success;
    }

    /// <summary>
    /// Announces that a fact-sheet run ended without a draft (02.5.3 plan §5): <see cref="FactSheetGenerationFailed"/>
    /// is raised for the UI's <c>EpisodeUpdated</c>. The status does not change, because it never changed.
    /// </summary>
    /// <param name="generationRunId">The run that failed.</param>
    /// <param name="code">The D8 job-outcome code the run ended with.</param>
    /// <param name="trigger">Who asked for the run.</param>
    /// <param name="now">The job's clock reading.</param>
    /// <returns><c>Success</c>; a validation error when the run id is empty.</returns>
    public ErrorOr<Success> RaiseFactSheetGenerationFailed(
        Guid generationRunId,
        GenerationErrorCode code,
        GenerationTrigger trigger,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        Raise(new FactSheetGenerationFailed(Id, SeriesId, UserId, generationRunId, code.Value, trigger, now));

        return Result.Success;
    }
}
