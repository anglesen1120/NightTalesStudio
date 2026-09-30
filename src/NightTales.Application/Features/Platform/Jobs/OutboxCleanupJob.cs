using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Platform.Jobs;

/// <summary>
/// Deletes the <c>outbox_messages</c> rows that were delivered more than <see cref="Retention"/> ago
/// (09.2.4 plan §3/§7). Rows the dispatcher still owes — pending and dead-lettered — are kept.
/// </summary>
/// <remarks>
/// The job is idempotent and safe to re-run: it deletes by <c>processed_at</c>, never by id, and the
/// batching lives in <see cref="IOutboxCleanupStore"/>.
/// </remarks>
public sealed class OutboxCleanupJob(IOutboxCleanupStore store, IClock clock) : IBackgroundJob
{
    /// <summary>How long a delivered row is kept for support and lag forensics.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Removes every row processed before <see cref="Retention"/> ago.</summary>
    public async Task RunAsync(CancellationToken cancellationToken) =>
        await store.DeleteProcessedBeforeAsync(clock.UtcNow - Retention, cancellationToken).ConfigureAwait(false);
}
