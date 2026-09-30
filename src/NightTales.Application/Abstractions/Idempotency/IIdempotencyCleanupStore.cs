namespace NightTales.Application.Abstractions.Idempotency;

/// <summary>
/// The expiry sweep of <c>idempotency_keys</c> (09.1.3 plan §7): it removes the rows whose replay window
/// is over, so the table stays bounded.
/// </summary>
/// <remarks>
/// It is a port of its own rather than a method of <see cref="IIdempotencyStore"/>: the request-path
/// store refuses to run outside the command's transaction (09 README D5/D6), while the sweep is a
/// maintenance write that joins no command and therefore no transaction.
/// </remarks>
public interface IIdempotencyCleanupStore
{
    /// <summary>
    /// Deletes every row that expired before <paramref name="cutoff"/>, in batches, and reports how many
    /// it removed.
    /// </summary>
    /// <param name="cutoff">The instant a row must have expired before; the caller adds its grace period.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
