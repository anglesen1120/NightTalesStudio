namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The <c>hub_tickets</c> table of 09 README BD11: the one row an issue writes, and the sweep
/// <c>HubTicketCleanupJob</c> runs hourly.
/// </summary>
/// <remarks>
/// <para>
/// The table never leaves Infrastructure — an Application handler cannot name it, and it holds no
/// aggregate — so both of its writers are behind this port, exactly like <c>IOutboxCleanupStore</c>.
/// The claim of a ticket is <em>not</em> here: it belongs to
/// <see cref="IHubTicketValidator"/> with the rest of the authentication path.
/// </para>
/// <para>
/// Retention and batch size are configuration (<c>Realtime:TicketCleanupRetentionHours</c>,
/// <c>Realtime:TicketCleanupBatchSize</c>), read by the implementation: the caller passes no
/// per-call policy, which is what keeps a job or a handler from inventing one.
/// </para>
/// </remarks>
public interface IHubTicketStore
{
    /// <summary>
    /// Stores <paramref name="ticketHash"/> for <paramref name="userId"/> with the configured TTL and
    /// returns the row's expiry.
    /// </summary>
    /// <remarks>
    /// In the caller's transaction: the D5 pipeline opens one around the command, so the insert and
    /// the opportunistic delete below commit or roll back together. The delete bounds this user's
    /// rows — an expired ticket is dead weight nobody will ever claim — to at most the rate limit's
    /// ten per minute, so the hourly sweep only ever sees other users' leftovers.
    /// </remarks>
    /// <param name="userId">The owner the ticket authenticates as.</param>
    /// <param name="ticketHash">The <c>SHA-256</c> hex of the ticket; never the plaintext.</param>
    /// <param name="cancellationToken">The command's token.</param>
    /// <returns>The instant the row stops being claimable.</returns>
    Task<DateTimeOffset> IssueAsync(Guid userId, string ticketHash, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes at most the configured batch of rows whose expiry is older than the configured
    /// retention, and reports how many it removed.
    /// </summary>
    /// <remarks>
    /// One batch per call — the design's <c>≤ Realtime:TicketCleanupBatchSize</c> per run — so a
    /// sweep's transaction stays short; the hourly schedule and the issue-time delete are what keep
    /// the table from ever holding a backlog.
    /// </remarks>
    /// <param name="cancellationToken">The job's token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken);
}
