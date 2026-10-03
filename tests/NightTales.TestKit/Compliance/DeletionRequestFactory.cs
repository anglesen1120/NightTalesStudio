using System.Globalization;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit.Database;

namespace NightTales.TestKit.Compliance;

/// <summary>Inserts <c>data_deletion_requests</c> rows in any state with raw SQL, so a test controls every column.</summary>
public static class DeletionRequestFactory
{
    /// <summary>Inserts one row and returns its id; <c>due_at</c> is <paramref name="requestedAt"/> plus the reason's deadline unless given.</summary>
    public static async Task<Guid> InsertAsync(
        TestDatabase database,
        Guid userId,
        DeletionScope scope,
        Guid? targetId,
        DeletionReason reason,
        DateTimeOffset requestedAt,
        DateTimeOffset? dueAt = null,
        DeletionStatus status = DeletionStatus.Pending,
        int attempts = 0,
        DateTimeOffset? claimedAt = null,
        DateTimeOffset? completedAt = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var id = Guid.CreateVersion7();
        var due = dueAt ?? requestedAt + DeletionRequest.DeadlineFor(reason);
        var state = status switch
        {
            DeletionStatus.Pending => "pending",
            DeletionStatus.InProgress => "in_progress",
            DeletionStatus.Completed => "completed",
            _ => "failed",
        };

        await database.ExecuteAsync(
            "INSERT INTO data_deletion_requests (id, user_id, scope, target_id, reason, status, requested_at, due_at, claimed_at, attempts, completed_at) "
            + $"VALUES ('{id}', '{userId}', '{scope.ToWire()}', {Literal(targetId)}, '{reason.ToWire()}', '{state}', "
            + $"{Literal(requestedAt)}, {Literal(due)}, {Literal(claimedAt)}, {attempts}, {Literal(completedAt)})",
            ct);

        return id;
    }

    private static string Literal(Guid? value) => value is null ? "NULL" : $"'{value}'";

    private static string Literal(DateTimeOffset? value) =>
        value is null ? "NULL" : $"'{value.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz";
}
