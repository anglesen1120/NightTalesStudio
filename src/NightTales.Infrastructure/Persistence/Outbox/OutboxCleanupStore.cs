using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Outbox;

namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// The processed-row sweep of <c>outbox_messages</c> (09.2.4 plan §3/§7): one <c>DELETE</c> per batch on the
/// scope's connection, no transaction of its own — every batch is atomic on its own and a re-run continues
/// where the previous one stopped.
/// </summary>
/// <remarks>
/// <para>
/// The predicate is <c>processed_at</c> only: pending rows (<c>processed_at IS NULL</c>, whether or not
/// <c>failed_at</c> is set) are work or evidence, never garbage.
/// </para>
/// <para>
/// As in <see cref="Idempotency.IdempotencyCleanupStore"/>, the expiry test is repeated on the row about to
/// be deleted and not only inside the subquery: the subquery's result is fixed when the statement starts,
/// so a row it named keeps its fate only if the outer qualifier also names it.
/// </para>
/// </remarks>
public sealed class OutboxCleanupStore(AppDbContext database) : IOutboxCleanupStore
{
    /// <summary>The rows one <c>DELETE</c> may take (09.2.4 plan §7).</summary>
    public const int BatchSize = 1_000;

    /// <inheritdoc />
    public async Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
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

    // {0} is deliberately used twice: the outer test is the one that decides the fate of the row, the inner
    // one only picks the batch (see the remarks).
    private const string DeleteBatchSql =
        """
        DELETE FROM outbox_messages
        WHERE processed_at IS NOT NULL
          AND processed_at < {0}
          AND id IN (
            SELECT id FROM outbox_messages
            WHERE processed_at IS NOT NULL
              AND processed_at < {0}
            ORDER BY processed_at
            LIMIT {1}
          )
        """;
}
