using Microsoft.EntityFrameworkCore;
using NightTales.Application.Audit;
using NightTales.Application.Features.Platform.Retention;
using NightTales.Domain.Audit;

namespace NightTales.Infrastructure.Persistence;

/// <summary>
/// The <c>audit_logs</c> side of <c>RetentionSweepJob</c> (09.7.3-T1): pages candidates by a keyset over
/// <c>(created_at, id)</c> — the <c>ix_audit_logs_created_id</c> index — and deletes expired ids in one
/// set-based statement.
/// </summary>
/// <remarks>
/// <para>
/// <c>AuditLog</c> is not <c>IUserOwned</c> (09.3.6 plan §55), so the table carries no tenant filter and
/// there is nothing to bypass: this type never calls <c>IgnoreQueryFilters</c> and is on no allow-list.
/// </para>
/// <para>
/// The scan is the row comparison <c>(created_at, id) &gt; (@after, @id)</c> ordered by the same two columns,
/// so PostgreSQL walks the index from the cursor and stops after <c>take</c> rows: no <c>OFFSET</c>, no
/// sort, no re-read (README §B10 WT47). The rule decision is not made here — it is made in C# over the
/// returned classification (<see cref="AuditRetentionPredicate"/>), so no retention window is encoded in SQL.
/// </para>
/// </remarks>
/// <param name="database">The scope's write context; only reads and one <c>ExecuteDelete</c> go through it.</param>
public sealed class AuditRetentionStore(AppDbContext database) : IAuditRetentionStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditRetentionCandidate>> LoadAfterAsync(
        DateTimeOffset? afterCreatedAt,
        Guid? afterId,
        int take,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var source = afterCreatedAt is { } createdAt && afterId is { } id
            ? database.AuditLogs.FromSql($"SELECT * FROM audit_logs WHERE (created_at, id) > ({createdAt}, {id})")
            : database.AuditLogs;

        var rows = await source
            .AsNoTracking()
            .OrderBy(log => log.CreatedAt)
            .ThenBy(log => log.Id)
            .Take(take)
            .Select(log => new { log.Id, log.Category, log.ActorType, log.Visibility, log.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // HasPaymentLink: `audit_logs` has no payment linkage column until plan 12 adds one. Stopgap (09.7.3-T1
        // SEC-2): every billing row counts as payment-linked, so the never-delete rule keeps it. Plan 12 MUST
        // replace this with the real link and may then release unlinked billing rows to the 365-day rule.
        return
        [
            .. rows.Select(row => new AuditRetentionCandidate(
                row.Id,
                new AuditRetentionRow(row.Category, row.ActorType, row.Visibility, HasPaymentLink: row.Category == Category.Billing, row.CreatedAt),
                row.CreatedAt)),
        ];
    }

    /// <inheritdoc />
    public async Task<int> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }

        var list = ids.ToList();

        return await database.AuditLogs
            .Where(log => list.Contains(log.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
