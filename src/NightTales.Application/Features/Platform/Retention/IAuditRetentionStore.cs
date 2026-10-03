using NightTales.Application.Audit;

namespace NightTales.Application.Features.Platform.Retention;

/// <summary>
/// One <c>audit_logs</c> row as the retention sweep sees it: its id, the classification the ordered rules
/// match on, and the instant that is both the age of the row and the keyset cursor (09.7.3-T1).
/// </summary>
/// <param name="Id">The row id.</param>
/// <param name="Row">The classification the rules of <see cref="AuditRetentionRules"/> evaluate.</param>
/// <param name="CreatedAt">When the row was written; the first half of the keyset cursor.</param>
public sealed record AuditRetentionCandidate(Guid Id, AuditRetentionRow Row, DateTimeOffset CreatedAt);

/// <summary>
/// The sweep's own port over <c>audit_logs</c> (09.7.3-T1). Audit rows are not <c>IUserOwned</c>, so the pinned
/// <c>ISystemWorkQuery.PendingAsync&lt;T&gt;</c> cannot serve them; the rule decision stays in C#
/// (<see cref="AuditRetentionPredicate"/>) and this port only pages candidates and deletes ids.
/// </summary>
public interface IAuditRetentionStore
{
    /// <summary>
    /// Reads the next <paramref name="take"/> candidates strictly after the cursor, ordered
    /// <c>created_at, id</c>: a keyset scan over <c>ix_audit_logs_created_id</c> with no <c>OFFSET</c> and no
    /// re-scan, so a cursor that advances to the last row always terminates (README §B10 WT47).
    /// </summary>
    /// <param name="afterCreatedAt">The cursor's instant, or <see langword="null"/> for the first page.</param>
    /// <param name="afterId">The cursor's id, or <see langword="null"/> for the first page.</param>
    /// <param name="take">The page size; must be positive.</param>
    /// <param name="ct">Cancels the statement.</param>
    Task<IReadOnlyList<AuditRetentionCandidate>> LoadAfterAsync(
        DateTimeOffset? afterCreatedAt,
        Guid? afterId,
        int take,
        CancellationToken ct);

    /// <summary>Deletes the rows with these ids in one set-based statement.</summary>
    /// <param name="ids">The ids the predicate judged expired.</param>
    /// <param name="ct">Cancels the statement.</param>
    /// <returns>The number of rows removed.</returns>
    Task<int> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
}
