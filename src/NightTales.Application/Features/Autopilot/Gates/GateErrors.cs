using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The errors of the gate engine (02.8.2 plan §"Failure handling"): what the writer, the recorder and the
/// approval service answer when they cannot do their job at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>A failure is not a verdict.</b> A story that did not pass its checks is a successful call whose
/// <see cref="StoryGateOutcome.AutoApproved"/> is <see langword="false"/>; these errors mean the gate could not
/// be evaluated or the approval could not be recorded, and the caller retries. They cover exactly that: the run
/// the caller names is gone, the episode it approved is gone, the caller asked to approve a verdict that did not
/// pass, or the approval's own audit row could not be committed
/// (<see cref="ApprovalAuditFailed(Guid, string)"/>).
/// </para>
/// <para>
/// <b>None of these reaches HTTP.</b> The gates are internal application services (plan §4: "not HTTP
/// endpoints ... No new route, no new error code in the HTTP catalogue"), so <see cref="GateNotPassed"/> is a
/// local code and deliberately not an addition to <see cref="ErrorCodes"/>. The two lookups answer the shared
/// <see cref="ErrorCodes.NotFound"/>, exactly as <c>IdeaErrors</c> does: the unknown, the foreign and the deleted
/// stay indistinguishable (09 README D10).
/// </para>
/// </remarks>
public static class GateErrors
{
    /// <summary>404: the run is unknown, or it belongs to another user (09 README D10).</summary>
    public static Error RunNotFound { get; } =
        Error.NotFound(ErrorCodes.NotFound, "The autopilot run does not exist.");

    /// <summary>404: the run's episode is unknown, or it belongs to another user.</summary>
    public static Error EpisodeNotFound { get; } =
        Error.NotFound(ErrorCodes.NotFound, "The episode does not exist.");

    /// <summary>
    /// The caller asked the approval service to send an approve command for a verdict that did not pass (or was
    /// held): refused, never approved. This is a caller bug — only a passing, unheld verdict may be approved —
    /// and it never becomes an episode state, because no command is sent.
    /// </summary>
    public static Error GateNotPassed { get; } =
        Error.Validation(
            "gate_not_passed",
            "A gate verdict that did not pass may not be approved.");

    /// <summary>
    /// The run is missing from a write the caller has already read it for: the row was deleted between the
    /// read and the write, or the caller passed a run of another tenant. Nothing is written.
    /// </summary>
    /// <param name="runId">The run the caller named.</param>
    /// <returns>The error the writer answers.</returns>
    public static Error RunNotWritable(Guid runId) =>
        Error.Conflict(
            "autopilot_run_not_writable",
            $"The autopilot run {runId} can no longer be written.");

    /// <summary>The run's episode is missing from an approval the caller has already evaluated.</summary>
    /// <param name="runId">The run whose episode is gone.</param>
    /// <returns>The error the approval service answers.</returns>
    public static Error EpisodeOfRunNotFound(Guid runId) =>
        Error.NotFound(
            ErrorCodes.NotFound,
            $"The episode of autopilot run {runId} does not exist.");

    /// <summary>The code of an approval whose audit row could not be committed (<see cref="ApprovalAuditFailed"/>).</summary>
    /// <remarks>
    /// The gates read it to tell this error apart from a refusal (02.8.2-T1 R2): the episode <em>is</em> approved,
    /// so it is never recorded as a failure and never queues the owner's notice.
    /// </remarks>
    public const string ApprovalAuditFailedCode = "approval_audit_failed";

    /// <summary>
    /// The approval was applied but its audit row could not be committed. The table is append-only and the row
    /// is the only record of who approved, so a lost one is answered as an error the caller retries — never
    /// logged away, and never a refusal: the gates surface it unchanged (<see cref="ApprovalAuditFailedCode"/>) so
    /// the retry can repair the record. The story half's retry reaches the replay path, which backfills the missing
    /// row when the approval really was the autopilot's; the video half has no such reconstruction (its command's
    /// own row records no approval source), so there the error is the record the operator acts on.
    /// </summary>
    /// <param name="episodeId">The episode whose approval could not be recorded.</param>
    /// <param name="action">The audit action that failed; a fixed code, never a stored string.</param>
    /// <returns>The error the approval service answers.</returns>
    public static Error ApprovalAuditFailed(Guid episodeId, string action) =>
        Error.Failure(
            ApprovalAuditFailedCode,
            $"The approval of episode {episodeId} was applied, but its audit row ({action}) could not be committed.");

    /// <summary>
    /// The verdict's asset is no longer the episode's final video, so the render it judged was replaced (a
    /// re-render was ingested after the gate read it). It is the guard that distinguishes a replay on a current
    /// asset from a verdict whose render the episode has moved past, even when the episode is already
    /// <c>ready</c>: without it a re-render would be approved on the old render's verdict, which is exactly the
    /// wrong pass the gate exists to prevent.
    /// </summary>
    /// <param name="episodeId">The episode whose final asset moved.</param>
    /// <returns>The error the approval service answers.</returns>
    public static Error StaleVideoVerdict(Guid episodeId) =>
        Error.Conflict(
            "video_verdict_stale",
            $"The episode {episodeId} no longer carries the asset the video gate evaluated.");

    /// <summary>
    /// The run's episode carries no current story version, so no story ETag can be built and nothing may be
    /// approved (02 README D9: the tag names the current version). 02.8.3-T1 links the version before the gate
    /// runs, so this is an infrastructure failure rather than a verdict.
    /// </summary>
    /// <param name="episodeId">The episode without a current story version.</param>
    /// <returns>The error the approval service answers.</returns>
    public static Error EpisodeWithoutStoryVersion(Guid episodeId) =>
        Error.Failure(
            "episode_without_story_version",
            $"The episode {episodeId} has no current story version, so no story ETag exists.");

    /// <summary>
    /// The run carries no episode at all: nothing can be approved and no notice can name one. It is the same
    /// case <see cref="AutopilotRun.RaiseOwnerActionRequired"/> throws on, answered as data here because a
    /// caller can act on it (02.8.3-T1 links the episode before the gate runs).
    /// </summary>
    /// <param name="runId">The run with no episode.</param>
    /// <returns>The error the gate answers.</returns>
    public static Error RunHasNoEpisode(Guid runId) =>
        Error.Failure(
            "autopilot_run_without_episode",
            $"The autopilot run {runId} has no episode, so no gate can evaluate it.");
}
