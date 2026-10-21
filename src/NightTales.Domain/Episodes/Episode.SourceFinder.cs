using ErrorOr;
using NightTales.Domain.Research.Events;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Domain.Episodes;

/// <summary>
/// The source-finder part of the episode state machine (02.7 plan §3/§4 U1). A find-sources run never changes
/// <see cref="Episode.Status"/>: <c>EpisodeStatus</c> has no source-finder value, only a <c>story</c> run moves an
/// episode, and the run row plus D2's "at most one active run per (episode, kind)" partial unique index are the
/// whole state. This method therefore only guards the precondition (the episode may be searched for:
/// <c>draft</c> or <c>story_review</c>) and raises <see cref="SourceFindRequested"/>.
/// </summary>
public sealed partial class Episode
{
    /// <summary>
    /// The longest query a find-sources run accepts (<c>SourceFinder</c> plan §3): the validator trims and strips
    /// control characters first, and a longer value is a 400 rather than a silent truncation.
    /// </summary>
    public const int MaxSourceFindQueryChars = 300;

    private Guid? _requestedSourceFindRunId;

    /// <summary>
    /// Starts a source search (02.7.2 plan §4 U1): the episode must be <c>draft</c> or <c>story_review</c> and
    /// <see cref="SourceFindRequested"/> is raised. The status does not change.
    /// </summary>
    /// <param name="generationRunId">The <c>generation_runs</c> row created in the same transaction.</param>
    /// <param name="trigger">Who asked.</param>
    /// <param name="query">
    /// The already-normalised query (trimmed, control characters stripped), or <see langword="null"/> when the
    /// caller typed nothing; it is the only copy that reaches storage (D19).
    /// </param>
    /// <param name="now">The command's clock reading.</param>
    /// <returns>
    /// <c>Success</c>; a repeat for the same run is also <c>Success</c> without a second event. Any other state
    /// is 409 <see cref="InvalidStateCode"/>; an out-of-contract query is a validation error.
    /// </returns>
    public ErrorOr<Success> RequestSourceFind(
        Guid generationRunId,
        GenerationTrigger trigger,
        string? query,
        DateTimeOffset now)
    {
        if (generationRunId == Guid.Empty)
        {
            return RunIdRequired();
        }

        if (_requestedSourceFindRunId == generationRunId)
        {
            return Result.Success;
        }

        if (!CanEditStory)
        {
            return Error.Conflict(
                InvalidStateCode,
                "Sources cannot be searched for in the episode's current state.");
        }

        // The command's validator is the caller-facing guard; this is the invariant the event payload carries.
        if (query is { Length: > MaxSourceFindQueryChars })
        {
            return Error.Validation(
                "source_find_query_too_long",
                $"A source-find query is at most {MaxSourceFindQueryChars} characters.");
        }

        _requestedSourceFindRunId = generationRunId;

        Raise(new SourceFindRequested(generationRunId, Id, SeriesId, UserId, trigger, query, now));

        return Result.Success;
    }
}
