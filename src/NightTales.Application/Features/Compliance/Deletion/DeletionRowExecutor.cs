using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>Who triggered an execution; the operator path writes an internal, labelled audit row.</summary>
public enum DeletionActor
{
    /// <summary>The hourly <c>DataDeletionJob</c>.</summary>
    Job,

    /// <summary>The <c>nts-worker deletions reapply</c> verb.</summary>
    Operator,
}

/// <summary>How one row's execution ended.</summary>
public enum DeletionOutcomeKind
{
    Completed,
    Failed,
    Missing,
    NotClaimed,
    Unhandled,
}

/// <summary>The result of one row; <paramref name="ErrorCode"/> is set for <see cref="DeletionOutcomeKind.Failed"/>.</summary>
public sealed record DeletionOutcome(DeletionOutcomeKind Kind, string? ErrorCode = null);

/// <summary>
/// Executes one claimed (<see cref="DeletionStatus.InProgress"/>) request exactly as 13.3.1 plan §4 UC2 steps 4-6 say:
/// a <b>fresh DI scope per row</b> pinned once with <c>ISystemContext.For(userId)</c>, the scope handler, then
/// complete + audit + commit in one transaction. Shared by the hourly job and the operator re-apply so both obey the
/// same rules. It never calls <c>IgnoreQueryFilters</c>: it only sees the pinned user's rows.
/// </summary>
public sealed partial class DeletionRowExecutor(
    IServiceScopeFactory scopes,
    IClock clock,
    DataDeletionOptions options,
    DataDeletionMetrics metrics,
    ILogger<DeletionRowExecutor> logger)
{
    private const string AuditAction = "account.data_deleted";
    private const string OperatorLabel = "system:operator";

    /// <summary>Runs the request; never throws except for cancellation, so one bad row cannot abort a batch.</summary>
    public async Task<DeletionOutcome> ExecuteAsync(
        Guid userId,
        Guid requestId,
        DeletionActor actor,
        CancellationToken cancellationToken)
    {
        string failureCode;
        DeletionScope scopeOfRow = default;

        try
        {
            using var scope = scopes.CreateScope();
            var provider = scope.ServiceProvider;

            // 09 README D10: pin first, resolve after.
            using var pinned = provider.GetRequiredService<ISystemContext>().For(userId);

            var database = provider.GetRequiredService<IAppDbContext>();
            var unit = provider.GetRequiredService<IUnitOfWork>();

            var row = await database.DeletionRequests
                .FirstOrDefaultAsync(request => request.Id == requestId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                LogMissing(requestId);

                return new DeletionOutcome(DeletionOutcomeKind.Missing);
            }

            scopeOfRow = row.Scope;

            if (row.Status != DeletionStatus.InProgress)
            {
                return new DeletionOutcome(DeletionOutcomeKind.NotClaimed);
            }

            var handler = provider.GetRequiredService<IDeletionScopeHandlerRegistry>().Resolve(row.Scope);
            if (handler is null)
            {
                var unregisteredScope = row.Scope.ToWire();
                LogUnregistered(unregisteredScope, requestId);

                return new DeletionOutcome(DeletionOutcomeKind.Unhandled);
            }

            await unit.BeginAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var result = await handler.ExecuteAsync(row, cancellationToken).ConfigureAwait(false);
                if (result.IsError)
                {
                    await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    failureCode = result.FirstError.Code;
                }
                else
                {
                    var now = clock.UtcNow;
                    var completed = row.Complete(now);
                    if (completed.IsError)
                    {
                        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);

                        return new DeletionOutcome(DeletionOutcomeKind.NotClaimed);
                    }

                    await provider.GetRequiredService<IAuditWriter>()
                        .WriteAsync(AuditFor(row, actor, now), cancellationToken)
                        .ConfigureAwait(false);

                    var commit = await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (!commit.IsError)
                    {
                        var summary = result.Value;
                        var completedScope = row.Scope.ToWire();
                        LogCompleted(completedScope, requestId, summary.ObjectsDeleted, summary.RowsAffected);

                        return new DeletionOutcome(DeletionOutcomeKind.Completed);
                    }

                    failureCode = commit.FirstError.Code;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await TryRollbackAsync(unit).ConfigureAwait(false);
                var thrownType = exception.GetType().Name;
                var thrownScope = row.Scope.ToWire();
                LogHandlerThrew(thrownType, thrownScope, requestId);
                failureCode = ErrorCodes.ServiceUnavailable;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The scope could not even be opened or the row not loaded: nothing to record on the row.
            var failedType = exception.GetType().Name;
            LogRowFailed(failedType, requestId);

            return new DeletionOutcome(DeletionOutcomeKind.Failed, ErrorCodes.ServiceUnavailable);
        }

        await RecordFailureAsync(userId, requestId, scopeOfRow, failureCode, cancellationToken).ConfigureAwait(false);

        return new DeletionOutcome(DeletionOutcomeKind.Failed, failureCode);
    }

    private async Task RecordFailureAsync(
        Guid userId,
        Guid requestId,
        DeletionScope scopeOfRow,
        string code,
        CancellationToken cancellationToken)
    {
        metrics.Failed(scopeOfRow.ToWire(), code);

        try
        {
            using var scope = scopes.CreateScope();
            var provider = scope.ServiceProvider;
            using var pinned = provider.GetRequiredService<ISystemContext>().For(userId);

            var database = provider.GetRequiredService<IAppDbContext>();
            var unit = provider.GetRequiredService<IUnitOfWork>();

            var row = await database.DeletionRequests
                .FirstOrDefaultAsync(request => request.Id == requestId, cancellationToken)
                .ConfigureAwait(false);

            if (row is null || row.Fail(clock.UtcNow, code, options.MaxAttempts).IsError)
            {
                return;
            }

            await unit.BeginAsync(cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (!row.HasAttemptsLeft(options.MaxAttempts))
            {
                var exhaustedScope = row.Scope.ToWire();
                var attempts = row.Attempts;
                LogExhausted(exhaustedScope, requestId, code, attempts);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The claim's lease will expire and the next run retries; the failure metric is already counted.
            var recordType = exception.GetType().Name;
            LogRecordFailed(recordType, requestId);
        }
    }

    private static async Task TryRollbackAsync(IUnitOfWork unit)
    {
        try
        {
            await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The scope's disposal rolls back an abandoned transaction anyway.
        }
    }

    private static AuditEntry AuditFor(DeletionRequest row, DeletionActor actor, DateTimeOffset now) =>
        new(
            ActorUserId: null,
            ActorType: ActorType.System,
            SubjectUserId: row.UserId,
            Action: AuditAction,
            Category: Category.Support,
            Visibility: actor == DeletionActor.Operator ? Visibility.Internal : Visibility.User,
            EntityType: "deletion_request",
            EntityId: row.Id,
            DataJson: JsonSerializer.Serialize(new
            {
                scope = row.Scope.ToWire(),
                targetId = row.TargetId,
                reason = row.Reason.ToWire(),
            }),
            Ip: null,
            UserAgent: null,
            ActorLabel: actor == DeletionActor.Operator ? OperatorLabel : null,
            OccurredAt: now);

    [LoggerMessage(EventId = 4300, Level = LogLevel.Information, Message = "data_deletion_row_completed scope={Scope} requestId={RequestId} objects={Objects} rows={Rows}")]
    private partial void LogCompleted(string scope, Guid requestId, int objects, int rows);

    [LoggerMessage(EventId = 4301, Level = LogLevel.Warning, Message = "data_deletion_target_missing requestId={RequestId}")]
    private partial void LogMissing(Guid requestId);

    [LoggerMessage(EventId = 4302, Level = LogLevel.Warning, Message = "data_deletion_scope_unregistered scope={Scope} requestId={RequestId}")]
    private partial void LogUnregistered(string scope, Guid requestId);

    [LoggerMessage(EventId = 4303, Level = LogLevel.Warning, Message = "data_deletion_handler_threw exception={ExceptionType} scope={Scope} requestId={RequestId}")]
    private partial void LogHandlerThrew(string exceptionType, string scope, Guid requestId);

    [LoggerMessage(EventId = 4304, Level = LogLevel.Error, Message = "data_deletion_row_failed exception={ExceptionType} requestId={RequestId}")]
    private partial void LogRowFailed(string exceptionType, Guid requestId);

    [LoggerMessage(EventId = 4305, Level = LogLevel.Error, Message = "data_deletion_exhausted scope={Scope} requestId={RequestId} errorCode={ErrorCode} attempts={Attempts}")]
    private partial void LogExhausted(string scope, Guid requestId, string errorCode, int attempts);

    [LoggerMessage(EventId = 4306, Level = LogLevel.Error, Message = "data_deletion_record_failure_failed exception={ExceptionType} requestId={RequestId}")]
    private partial void LogRecordFailed(string exceptionType, Guid requestId);
}
