using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// One gate stop, committed as one transaction (02.8.2 plan §4 UC6/UC7): the verdict lands on
/// <c>autopilot_runs.gate_results</c>, the owner's notice is queued, and the two commit or roll back together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a recorder and a writer.</b> <see cref="GateResultWriter"/> only touches the run row, so it can be
/// exercised on its own. This class is the unit of work the plan's sequence diagram shows: it opens the
/// transaction the caller (the gate evaluator, which is not a Mediator handler) has to open for
/// <c>IAuditWriter</c> and the outbox, writes the verdict, queues the notice, and commits once. A failure
/// anywhere rolls the whole stop back, so a run never holds a verdict without its notice.
/// </para>
/// <para>
/// <b>A live pass notifies nobody.</b> <paramref name="notifyReason"/> <see langword="null"/> means "the gate did
/// not stop": the verdict is still recorded — that is the evidence T3's calibration reads — and no
/// <c>autopilot.owner_action_required</c> row, e-mail or audit row is written.
/// </para>
/// <para>
/// <b>One stop, one notice.</b> The writer answers whether the run already held exactly this verdict
/// (<see cref="GateWriteResult"/>, 02.8.2-T1 F8/SEC-9). A re-evaluation of the same stop — the tick retrying after
/// the verdict committed, or a later read of the same attempt — records the verdict and notifies nobody, so the
/// owner gets one e-mail, one audit row and one outbox row per stop. A changed verdict (another reason, another
/// attempt, another mode) is a new stop and does notify.
/// </para>
/// <para>
/// <b>The reason is a code, never a sentence.</b> The notice carries one of <see cref="OwnerActionReasons"/> and
/// the failing check ids; the e-mail template owns the words (plan §6).
/// </para>
/// </remarks>
/// <param name="writer">The <c>gate_results</c> read-modify-write.</param>
/// <param name="notifications">The owner-action notice of one stop.</param>
/// <param name="unitOfWork">The transaction that pairs the verdict with the notice.</param>
/// <param name="logger">The ids-only log of this service.</param>
public sealed partial class GateOutcomeRecorder(
    GateResultWriter writer,
    AutopilotNotificationService notifications,
    IUnitOfWork unitOfWork,
    ILogger<GateOutcomeRecorder> logger)
{
    /// <summary>
    /// Records one story evaluation: the verdict, its context, and — when the gate stopped — the owner's notice,
    /// in one transaction.
    /// </summary>
    /// <param name="runId">The run that was evaluated.</param>
    /// <param name="result">The Domain verdict.</param>
    /// <param name="evidence">The context lines stored beside the verdict.</param>
    /// <param name="mode">The mode of this evaluation.</param>
    /// <param name="profile">The effective profile name.</param>
    /// <param name="notifyReason">
    /// Why the gate stopped, or <see langword="null"/> for a live pass. <see cref="GateStopReason.None"/> is not a
    /// stop: pass <see langword="null"/> instead.
    /// </param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>Success, or the first error that rolled the stop back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="evidence"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="notifyReason"/> is <see cref="GateStopReason.None"/>.</exception>
    public async ValueTask<ErrorOr<Success>> RecordStoryAsync(
        Guid runId,
        StoryGateResult result,
        GateStoryEvidence evidence,
        GateMode mode,
        string profile,
        GateStopReason? notifyReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(evidence);

        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var written = await writer
            .WriteStoryAsync(runId, result, evidence, mode, profile, ct)
            .ConfigureAwait(false);

        if (written.IsError)
        {
            return await RollbackAsync(written.Errors, ct).ConfigureAwait(false);
        }

        // The run already held this verdict: the notice for this stop is the one it already carries, and a second
        // one would be a duplicate (02.8.2-T1 F8).
        if (written.Value == GateWriteResult.Written)
        {
            var notified = await NotifyAsync(runId, StoppedReason(notifyReason, result), result.Failures, ct)
                .ConfigureAwait(false);

            if (notified.IsError)
            {
                return await RollbackAsync(notified.Errors, ct).ConfigureAwait(false);
            }
        }

        var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        if (committed.IsError)
        {
            return committed.Errors;
        }

        // The wire name is read once: the log call stays cheap even when its level is off (CA1873).
        var wireMode = mode.ToWire();
        LogStoryRecorded(
            runId,
            result.Attempt,
            wireMode,
            written.Value,
            written.Value == GateWriteResult.Written && notifyReason is not null,
            result.Failures.Count);

        return Result.Success;
    }

    /// <summary>
    /// Records one video evaluation the same way: the verdict, then — when the gate stopped — the owner's notice,
    /// in one transaction.
    /// </summary>
    /// <param name="runId">The run whose render was evaluated.</param>
    /// <param name="result">The Domain verdict.</param>
    /// <param name="mode">The mode of this evaluation.</param>
    /// <param name="profile">The effective profile name.</param>
    /// <param name="notifyReason">Why the gate stopped, or <see langword="null"/> for a live pass.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>Success, or the first error that rolled the stop back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="notifyReason"/> is <see cref="GateStopReason.None"/>.</exception>
    public async ValueTask<ErrorOr<Success>> RecordVideoAsync(
        Guid runId,
        VideoGateResult result,
        GateMode mode,
        string profile,
        GateStopReason? notifyReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var written = await writer
            .WriteVideoAsync(runId, result, mode, profile, ct)
            .ConfigureAwait(false);

        if (written.IsError)
        {
            return await RollbackAsync(written.Errors, ct).ConfigureAwait(false);
        }

        // The run already held this verdict: the notice for this stop is the one it already carries (02.8.2-T1 F8).
        if (written.Value == GateWriteResult.Written)
        {
            var notified = await NotifyAsync(runId, StoppedReason(notifyReason, null), result.Failures, ct)
                .ConfigureAwait(false);

            if (notified.IsError)
            {
                return await RollbackAsync(notified.Errors, ct).ConfigureAwait(false);
            }
        }

        var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        if (committed.IsError)
        {
            return committed.Errors;
        }

        // The wire name is read once: the log call stays cheap even when its level is off (CA1873).
        var wireMode = mode.ToWire();
        LogVideoRecorded(
            runId,
            wireMode,
            written.Value,
            written.Value == GateWriteResult.Written && notifyReason is not null,
            result.Failures.Count);

        return Result.Success;
    }

    /// <summary>
    /// Records a refusal of the approval a passing verdict asked for (plan §4 UC5): the failure code is appended
    /// to the stored verdict, the owner is told to approve it themselves
    /// (<see cref="OwnerActionReasons.OwnerApprovalRequired"/>), and one transaction commits both.
    /// </summary>
    /// <param name="runId">The run whose approval was refused.</param>
    /// <param name="gate">The gate whose verdict is amended: <see cref="GateResultWriter.StoryGate"/> or <see cref="GateResultWriter.VideoGate"/>.</param>
    /// <param name="failureCode">The code to record, for example <c>etag_conflict</c>.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>Success, or the first error that rolled the record back.</returns>
    /// <exception cref="ArgumentException"><paramref name="gate"/> or <paramref name="failureCode"/> is blank.</exception>
    public async ValueTask<ErrorOr<Success>> RecordApprovalFailureAsync(
        Guid runId,
        string gate,
        string failureCode,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);

        await unitOfWork.BeginAsync(ct).ConfigureAwait(false);

        var appended = await writer
            .AppendFailureAsync(runId, gate, failureCode, ct)
            .ConfigureAwait(false);

        if (appended.IsError)
        {
            return await RollbackAsync(appended.Errors, ct).ConfigureAwait(false);
        }

        // The run already carried this refusal: it is the one the owner was told about (02.8.2-T1 F8).
        if (appended.Value == GateWriteResult.Written)
        {
            var notified = await NotifyAsync(
                    runId,
                    OwnerActionReasons.OwnerApprovalRequired,
                    [failureCode],
                    ct)
                .ConfigureAwait(false);

            if (notified.IsError)
            {
                return await RollbackAsync(notified.Errors, ct).ConfigureAwait(false);
            }
        }

        var committed = await unitOfWork.CommitAsync(ct).ConfigureAwait(false);

        if (committed.IsError)
        {
            return committed.Errors;
        }

        LogApprovalFailureRecorded(runId, gate, failureCode, appended.Value);

        return Result.Success;
    }

    /// <summary>
    /// The notice code of a stop, or <see langword="null"/> when the gate did not stop.
    /// </summary>
    /// <remarks>
    /// A failed check whose verdict carries the <c>moderator_unavailable</c> detail is reported as the moderator
    /// outage rather than as a generic failure, so the owner reads the reason that is true even when the caller
    /// passed the coarse code (plan §3.1 check 7, plan §6's more specific copy line).
    /// </remarks>
    /// <param name="reason">The stop the caller asked for, or <see langword="null"/>.</param>
    /// <param name="result">The story verdict, when there is one; the video half has no moderator check.</param>
    /// <returns>One of <see cref="OwnerActionReasons"/>, or <see langword="null"/> for a live pass.</returns>
    internal static string? StoppedReason(GateStopReason? reason, StoryGateResult? result)
    {
        if (reason is not { } stop)
        {
            return null;
        }

        if (stop == GateStopReason.FailedChecks && ModeratorWasUnavailable(result))
        {
            return OwnerActionReasons.ModeratorUnavailable;
        }

        return OwnerActionReasonOf(stop);
    }

    /// <summary>The notice code of one stop; <see cref="GateStopReason.None"/> is refused, because it is not a stop.</summary>
    /// <param name="reason">A stopping reason.</param>
    /// <returns>One of <see cref="OwnerActionReasons"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is <see cref="GateStopReason.None"/> or unknown.</exception>
    internal static string OwnerActionReasonOf(GateStopReason reason) => reason switch
    {
        GateStopReason.DryRun => OwnerActionReasons.DryRun,
        GateStopReason.Hold => OwnerActionReasons.TrueCrimeHold,
        GateStopReason.FailedChecks => OwnerActionReasons.FailedChecks,
        GateStopReason.OwnerApprovalRequired => OwnerActionReasons.OwnerApprovalRequired,
        GateStopReason.ModeratorUnavailable => OwnerActionReasons.ModeratorUnavailable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "A gate that did not stop carries no reason: pass null."),
    };

    private static bool ModeratorWasUnavailable(StoryGateResult? result) =>
        result?.Checks.Any(check =>
            check.Id == StoryGateCheckIds.ModerationGate && check.Detail == GateDetailCodes.ModeratorUnavailable)
        ?? false;

    private async ValueTask<ErrorOr<Success>> NotifyAsync(
        Guid runId,
        string? reason,
        IReadOnlyList<string> failures,
        CancellationToken ct)
    {
        if (reason is null)
        {
            return Result.Success;
        }

        // A stop that failed no check carries the failing check the run log shows; an empty list is honest there
        // (the dry run and the true-crime hold stop by design).
        return await notifications
            .NotifyOwnerActionRequiredAsync(runId, reason, failures, ct)
            .ConfigureAwait(false);
    }

    private async ValueTask<ErrorOr<Success>> RollbackAsync(List<Error> errors, CancellationToken ct)
    {
        await unitOfWork.RollbackAsync(ct).ConfigureAwait(false);

        LogRolledBack(errors.Count, errors[0].Code);

        return errors;
    }

    [LoggerMessage(
        EventId = 6440,
        Level = LogLevel.Information,
        Message = "gate_story_recorded runId={RunId} attempt={Attempt} mode={Mode} write={Write} notified={Notified} failures={FailureCount}")]
    private partial void LogStoryRecorded(Guid runId, int attempt, string mode, GateWriteResult write, bool notified, int failureCount);

    [LoggerMessage(
        EventId = 6441,
        Level = LogLevel.Information,
        Message = "gate_video_recorded runId={RunId} mode={Mode} write={Write} notified={Notified} failures={FailureCount}")]
    private partial void LogVideoRecorded(Guid runId, string mode, GateWriteResult write, bool notified, int failureCount);

    [LoggerMessage(
        EventId = 6442,
        Level = LogLevel.Warning,
        Message = "gate_recording_rolled_back errorCount={ErrorCount} firstCode={FirstCode}")]
    private partial void LogRolledBack(int errorCount, string firstCode);

    [LoggerMessage(
        EventId = 6443,
        Level = LogLevel.Warning,
        Message = "gate_approval_failure_recorded runId={RunId} gate={Gate} failureCode={FailureCode} write={Write}")]
    private partial void LogApprovalFailureRecorded(Guid runId, string gate, string failureCode, GateWriteResult write);
}
