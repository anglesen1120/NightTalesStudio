using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Idempotency;

namespace NightTales.Infrastructure.Persistence.Idempotency;

/// <summary>
/// The expiry sweep of <c>idempotency_keys</c> (09 README D6, 09.1.3 plan §7): one <c>DELETE</c> per
/// batch on the scope's connection, no transaction of its own — every batch is atomic on its own and a
/// re-run continues where the previous one stopped.
/// </summary>
/// <remarks>
/// <para>
/// It runs outside any command transaction on purpose: a sweep that joined one would keep the command's
/// locks until the sweep finished, and a crash would have nothing to roll back that matters — the next
/// run deletes the rest.
/// </para>
/// <para>
/// A batch repeats the expiry test on the row it is about to delete, not only inside its subquery. The
/// subquery's result is fixed when the statement starts, so a row it named can still be renewed by a
/// twin's claim (<c>ReuseAsync</c> of <see cref="IdempotencyStore"/>) while this statement waits on that
/// row's lock; when the twin commits, <c>ReadCommitted</c> re-evaluates only the qualifiers that read
/// the target row (EvalPlanQual) and does not re-run the subquery. Without the outer test the sweep
/// would delete the renewed row and the claim that owns it would vanish under its owner.
/// </para>
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

    // {0} is deliberately used twice: the outer test is the one EvalPlanQual re-evaluates against a
    // concurrently renewed row, the inner one only picks the batch (see the remarks).
    private const string DeleteBatchSql =
        """
        DELETE FROM idempotency_keys
        WHERE expires_at < {0}
          AND id IN (
            SELECT id FROM idempotency_keys
            WHERE expires_at < {0}
            ORDER BY expires_at
            LIMIT {1}
        )
        """;
}
