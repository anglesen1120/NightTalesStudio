using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Idempotency;

namespace NightTales.Infrastructure.Persistence.Idempotency;

/// <summary>
/// The expiry sweep of <c>idempotency_keys</c> (09 README D6, 09.1.3 plan §7): one <c>DELETE</c> per
/// batch on the scope's connection, no transaction of its own — every batch is atomic on its own and a
/// re-run continues where the previous one stopped.
/// </summary>
/// <remarks>
/// It runs outside any command transaction on purpose: a sweep that joined one would keep the command's
/// locks until the sweep finished, and a crash would have nothing to roll back that matters — the next
/// run deletes the rest.
/// </remarks>
public sealed class IdempotencyCleanupStore(AppDbContext database) : IIdempotencyCleanupStore
{
    /// <summary>The rows one <c>DELETE</c> may take (09.1.3 plan §7).</summary>
    public const int BatchSize = 1_000;

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;
        int batch;

        do
        {
            batch = await database.Database
                .ExecuteSqlRawAsync(DeleteBatchSql, [cutoff, BatchSize], cancellationToken)
                .ConfigureAwait(false);
            deleted += batch;
        }
        while (batch == BatchSize);

        return deleted;
    }

    private const string DeleteBatchSql =
        """
        DELETE FROM idempotency_keys
        WHERE id IN (
            SELECT id FROM idempotency_keys
            WHERE expires_at < {0}
            ORDER BY expires_at
            LIMIT {1}
        )
        """;
}
