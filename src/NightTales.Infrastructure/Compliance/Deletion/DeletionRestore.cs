using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Compliance.Deletion;

/// <summary>
/// The restore-drill pair behind <c>nts-worker deletions export|reapply</c> (13.3.1 plan §5.3, 13 README YR12).
/// The export is a read-only raw-SQL <c>SELECT</c> of ids and dates (an operator read across users that needs no
/// <c>IgnoreQueryFilters</c>); the re-apply prepares each row in its owner's pinned scope and runs the very same
/// <see cref="DeletionRowExecutor"/> as the hourly job.
/// </summary>
public sealed class DeletionRestore(
    IServiceScopeFactory scopes,
    DeletionRowExecutor executor,
    IClock clock) : IDeletionRestore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DeletionExportRow>> ExportAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await database.Database.SqlQuery<ExportRecord>(
            $"""
            SELECT id, user_id, scope, target_id, reason, requested_at, due_at, completed_at
            FROM data_deletion_requests
            WHERE completed_at > {since} OR completed_at IS NULL
            ORDER BY completed_at NULLS FIRST, id
            """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new DeletionExportRow(
            row.Id, row.UserId, row.Scope, row.TargetId, row.Reason, row.RequestedAt, row.DueAt, row.CompletedAt))];
    }

    /// <inheritdoc />
    public async Task<DeletionReapplyReport> ReapplyAsync(IReadOnlyList<DeletionExportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var applied = 0;
        var failed = 0;
        var problems = new List<string>();

        var ordered = rows
            .OrderBy(row => row.CompletedAt ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.RequestedAt)
            .ThenBy(row => row.Id);

        foreach (var row in ordered)
        {
            var now = clock.UtcNow;

            if (!DeletionWire.TryParseScope(row.Scope, out var scope) || !DeletionWire.TryParseReason(row.Reason, out var reason))
            {
                failed++;
                problems.Add($"{row.Id}: unknown scope or reason");

                continue;
            }

            var execute = row.CompletedAt is not null || row.DueAt <= now;

            if (execute && !HasHandler(scope))
            {
                failed++;
                problems.Add($"{row.Id}: no handler registered for scope '{row.Scope}'");

                continue;
            }

            var prepared = await PrepareAsync(row, scope, reason, execute, now, cancellationToken).ConfigureAwait(false);
            if (prepared is not null)
            {
                failed++;
                problems.Add($"{row.Id}: {prepared}");

                continue;
            }

            if (!execute)
            {
                applied++;

                continue;
            }

            var outcome = await executor.ExecuteAsync(row.UserId, await RowIdAsync(row, scope, reason, cancellationToken).ConfigureAwait(false), DeletionActor.Operator, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Kind == DeletionOutcomeKind.Completed)
            {
                applied++;
            }
            else
            {
                failed++;
                problems.Add($"{row.Id}: {outcome.Kind}{(outcome.ErrorCode is null ? string.Empty : " " + outcome.ErrorCode)}");
            }
        }

        return new DeletionReapplyReport(applied, failed, problems);
    }

    private bool HasHandler(DeletionScope scope)
    {
        using var scoped = scopes.CreateScope();

        return scoped.ServiceProvider.GetRequiredService<IDeletionScopeHandlerRegistry>().RegisteredScopes.Contains(scope);
    }

    /// <summary>The id of the row to execute: the file's id, or the id of an open twin the restored database already holds.</summary>
    private async Task<Guid> RowIdAsync(DeletionExportRow row, DeletionScope scope, DeletionReason reason, CancellationToken cancellationToken)
    {
        using var scoped = scopes.CreateScope();
        using var pinned = scoped.ServiceProvider.GetRequiredService<ISystemContext>().For(row.UserId);
        var database = scoped.ServiceProvider.GetRequiredService<IAppDbContext>();

        var byId = await database.DeletionRequests.AsNoTracking()
            .Where(request => request.Id == row.Id)
            .Select(request => (Guid?)request.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (byId is not null)
        {
            return byId.Value;
        }

        return await database.DeletionRequests.AsNoTracking()
            .Where(request => request.UserId == row.UserId && request.Scope == scope && request.TargetId == row.TargetId
                && request.Reason == reason && request.CompletedAt == null)
            .Select(request => request.Id)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the database hold the row: inserts a missing one (id and dates preserved) and, when it is to be executed,
    /// moves it to in-progress (completed rows through <c>Reapply</c>). Returns a problem text, or <c>null</c>.
    /// </summary>
    private async Task<string?> PrepareAsync(
        DeletionExportRow row,
        DeletionScope scope,
        DeletionReason reason,
        bool execute,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scoped = scopes.CreateScope();
            using var pinned = scoped.ServiceProvider.GetRequiredService<ISystemContext>().For(row.UserId);
            var database = scoped.ServiceProvider.GetRequiredService<IAppDbContext>();
            var unit = scoped.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var existing = await database.DeletionRequests
                .Where(request => request.Id == row.Id
                    || (request.UserId == row.UserId && request.Scope == scope && request.TargetId == row.TargetId
                        && request.Reason == reason && request.CompletedAt == null))
                .OrderByDescending(request => request.Id == row.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                var restored = DeletionRequest.Restore(row.Id, row.UserId, scope, row.TargetId, reason, row.RequestedAt, row.DueAt, now);
                if (restored.IsError)
                {
                    return restored.FirstError.Description;
                }

                existing = restored.Value;
                database.DeletionRequests.Add(existing);
            }

            if (execute)
            {
                ErrorOr<Success> moved = existing.Status switch
                {
                    DeletionStatus.Completed => existing.Reapply(now),
                    DeletionStatus.InProgress => Result.Success,
                    _ => existing.Claim(now),
                };

                if (moved.IsError)
                {
                    return moved.FirstError.Code;
                }
            }

            await unit.BeginAsync(cancellationToken).ConfigureAwait(false);
            var commit = await unit.CommitAsync(cancellationToken).ConfigureAwait(false);

            return commit.IsError ? commit.FirstError.Code : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception.GetType().Name;
        }
    }

    private sealed record ExportRecord(
        Guid Id,
        Guid UserId,
        string Scope,
        Guid? TargetId,
        string Reason,
        DateTimeOffset RequestedAt,
        DateTimeOffset DueAt,
        DateTimeOffset? CompletedAt);
}
