using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Compliance.Deletion;

/// <summary>
/// One legal deletion duty (13.3.1 plan §3.1, 00 overview §6 <c>data_deletion_requests</c>). User-owned, so the
/// tenant filter protects the re-read of <c>CreateAsync</c> and every later reader; the job's cross-user read is
/// the allow-listed bypass in <c>DataDeletionJob</c>.
/// </summary>
/// <remarks>
/// Invariants: <c>DueAt == RequestedAt + deadline(Reason)</c>; <c>TargetId</c> is null iff the scope is
/// <see cref="DeletionScope.Account"/>; <c>CompletedAt</c> is set iff the status is
/// <see cref="DeletionStatus.Completed"/>; a completed row re-enters <see cref="DeletionStatus.InProgress"/> only
/// through <see cref="Reapply"/>. There is deliberately no alert-dedupe state: the due-soon alert is a monitoring
/// rule over a metric.
/// </remarks>
public sealed class DeletionRequest : AggregateRoot, IUserOwned
{
    /// <summary>Deadline of every reason except <see cref="DeletionReason.GoogleRevoke"/>.</summary>
    public static readonly TimeSpan StandardDeadline = TimeSpan.FromDays(7);

    /// <summary>Deadline of <see cref="DeletionReason.GoogleRevoke"/> (YouTube API Services III.D.2).</summary>
    public static readonly TimeSpan GoogleRevokeDeadline = TimeSpan.FromDays(30);

    private DeletionRequest()
    {
    }

    public Guid UserId { get; private set; }

    public DeletionScope Scope { get; private set; }

    public Guid? TargetId { get; private set; }

    public DeletionReason Reason { get; private set; }

    public DeletionStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastErrorCode { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? ReappliedAt { get; private set; }

    /// <summary>The legal deadline of <paramref name="reason"/>.</summary>
    public static TimeSpan DeadlineFor(DeletionReason reason) =>
        reason == DeletionReason.GoogleRevoke ? GoogleRevokeDeadline : StandardDeadline;

    /// <summary>Starts the clock: validates the scope/target/reason matrix and computes <see cref="DueAt"/>.</summary>
    public static ErrorOr<DeletionRequest> Create(
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        DateTimeOffset now)
    {
        var requestedAt = now;

        return Build(Guid.CreateVersion7(), userId, scope, targetId, reason, requestedAt, requestedAt + DeadlineFor(reason));
    }

    /// <summary>
    /// Re-creates a row an operator export names but the restored database lacks (<c>deletions reapply</c>): id and
    /// original dates are kept, the row is <see cref="DeletionStatus.Pending"/> and marked reapplied at
    /// <paramref name="now"/>. The deadline invariant is enforced, so a tampered file is refused.
    /// </summary>
    public static ErrorOr<DeletionRequest> Restore(
        Guid id,
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        DateTimeOffset requestedAt,
        DateTimeOffset dueAt,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            return Invalid("A restored request needs its id.");
        }

        if (dueAt != requestedAt + DeadlineFor(reason))
        {
            return Invalid("due_at must equal requested_at plus the deadline of the reason.");
        }

        var built = Build(id, userId, scope, targetId, reason, requestedAt, dueAt);
        if (built.IsError)
        {
            return built;
        }

        built.Value.ReappliedAt = now;

        return built;
    }

    /// <summary><see cref="DeletionStatus.Pending"/>/<see cref="DeletionStatus.Failed"/> to <see cref="DeletionStatus.InProgress"/>; counts an attempt.</summary>
    public ErrorOr<Success> Claim(DateTimeOffset now)
    {
        if (Status is not (DeletionStatus.Pending or DeletionStatus.Failed))
        {
            return InvalidTransition(nameof(Claim));
        }

        Status = DeletionStatus.InProgress;
        ClaimedAt = now;
        Attempts++;

        return Result.Success;
    }

    /// <summary><see cref="DeletionStatus.InProgress"/> to <see cref="DeletionStatus.Completed"/>.</summary>
    public ErrorOr<Success> Complete(DateTimeOffset now)
    {
        if (Status != DeletionStatus.InProgress)
        {
            return InvalidTransition(nameof(Complete));
        }

        Status = DeletionStatus.Completed;
        CompletedAt = now;
        LastErrorCode = null;
        LastErrorAt = null;

        return Result.Success;
    }

    /// <summary>
    /// <see cref="DeletionStatus.InProgress"/> to <see cref="DeletionStatus.Failed"/> with an error code (never a
    /// message). The row stays failed; <see cref="HasAttemptsLeft"/> says whether the hourly job retries it.
    /// </summary>
    public ErrorOr<Success> Fail(DateTimeOffset now, string errorCode, int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        if (Status != DeletionStatus.InProgress)
        {
            return InvalidTransition(nameof(Fail));
        }

        Status = DeletionStatus.Failed;
        LastErrorCode = errorCode;
        LastErrorAt = now;

        return Result.Success;
    }

    /// <summary>Whether a failed row may still be claimed (<see cref="Attempts"/> below <paramref name="maxAttempts"/>).</summary>
    public bool HasAttemptsLeft(int maxAttempts) => Attempts < maxAttempts;

    /// <summary>
    /// The restore re-apply: <see cref="DeletionStatus.Completed"/> to <see cref="DeletionStatus.InProgress"/> with
    /// <see cref="ReappliedAt"/> set and <see cref="CompletedAt"/> cleared; the only way out of completed.
    /// </summary>
    public ErrorOr<Success> Reapply(DateTimeOffset now)
    {
        if (Status != DeletionStatus.Completed)
        {
            return InvalidTransition(nameof(Reapply));
        }

        Status = DeletionStatus.InProgress;
        CompletedAt = null;
        ClaimedAt = now;
        ReappliedAt = now;
        Attempts++;

        return Result.Success;
    }

    private static ErrorOr<DeletionRequest> Build(
        Guid id,
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        DateTimeOffset requestedAt,
        DateTimeOffset dueAt)
    {
        if (userId == Guid.Empty)
        {
            return Invalid("A deletion request needs a user.");
        }

        if (!Enum.IsDefined(scope) || !Enum.IsDefined(reason))
        {
            return Invalid("Unknown scope or reason.");
        }

        var needsTarget = scope is DeletionScope.Channel or DeletionScope.Episode;
        if (needsTarget && (targetId is null || targetId == Guid.Empty))
        {
            return Invalid("This scope needs a target id.");
        }

        if (!needsTarget && targetId is not null)
        {
            return Invalid("The account scope takes no target id.");
        }

        if (reason == DeletionReason.AccountDelete && scope != DeletionScope.Account)
        {
            return Invalid("account_delete applies to the account scope only.");
        }

        if (reason is DeletionReason.InAppRevoke or DeletionReason.GoogleRevoke && scope != DeletionScope.Channel)
        {
            return Invalid("A revoke applies to the channel scope only.");
        }

        return new DeletionRequest
        {
            Id = id,
            UserId = userId,
            Scope = scope,
            TargetId = targetId,
            Reason = reason,
            Status = DeletionStatus.Pending,
            RequestedAt = requestedAt,
            DueAt = dueAt,
        };
    }

    private static Error Invalid(string description) =>
        Error.Validation(code: "validation_failed", description: description);

    /// <summary>An internal-only code: no transition error ever reaches HTTP, so the catalogue has no entry.</summary>
    private static Error InvalidTransition(string transition) =>
        Error.Conflict(code: "deletion_request_invalid_state", description: $"'{transition}' is not allowed in this state.");
}
