namespace NightTales.Application.Abstractions.Outbox;

/// <summary>
/// The processed-row sweep of <c>outbox_messages</c> (09.2.4 plan §3/§7): it removes the rows that were
/// delivered and are older than the retention, so the table stays bounded.
/// </summary>
/// <remarks>
/// Only <c>processed_at</c> rows are ever deleted: a pending row (<c>processed_at IS NULL</c>) is work the
/// Worker still owes, and a dead-lettered row (<c>failed_at</c> set) is evidence somebody has to look at.
/// The sweep is a maintenance write that joins no command, so it runs outside any transaction.
/// </remarks>
public interface IOutboxCleanupStore
{
    /// <summary>
    /// Deletes every delivered row processed before <paramref name="cutoff"/>, in batches, and reports how
    /// many it removed.
    /// </summary>
    /// <param name="cutoff">The instant a row must have been processed before; the caller adds its retention.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
