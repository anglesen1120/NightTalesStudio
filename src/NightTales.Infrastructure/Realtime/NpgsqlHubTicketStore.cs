using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// The <c>hub_tickets</c> writer of 09 README BD11: the issue path's one insert plus its
/// opportunistic delete, and the hourly sweep of <c>HubTicketCleanupJob</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both limits live in <see cref="RealtimeOptions"/> (<c>Realtime:TicketTtlSeconds</c>,
/// <c>Realtime:TicketCleanupRetentionHours</c>, <c>Realtime:TicketCleanupBatchSize</c>), so the
/// handler and the job carry no policy of their own and a changed limit is a configuration edit.
/// </para>
/// <para>
/// The expired-row delete of <see cref="IssueAsync"/> runs on the caller's connection: inside a
/// command the D5 pipeline's transaction is open, so it commits with the insert. It exists because a
/// user's rows would otherwise accumulate one per issue for 60 seconds past the TTL — the rate limit
/// bounds them to ten a minute, and this delete removes the ones already dead.
/// </para>
/// </remarks>
/// <param name="database">The tracked write model, whose connection the statements use.</param>
/// <param name="options">The real-time configuration.</param>
/// <param name="clock">The clock the expiry and the cutoff are computed from (D3).</param>
public sealed class NpgsqlHubTicketStore(
    AppDbContext database,
    IOptions<RealtimeOptions> options,
    IClock clock) : IHubTicketStore
{
    /// <inheritdoc />
    public async Task<DateTimeOffset> IssueAsync(
        Guid userId,
        string ticketHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketHash);
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);

        var live = options.Value;
        var now = clock.UtcNow;
        var expiresAt = now + live.TicketTtl;

        await database.Database
            .ExecuteSqlAsync(
                $"DELETE FROM hub_tickets WHERE user_id = {userId} AND expires_at < {now}",
                cancellationToken)
            .ConfigureAwait(false);

        database.HubTickets.Add(HubTicket.Create(ticketHash, userId, now, expiresAt));

        return expiresAt;
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var live = options.Value;
        var cutoff = clock.UtcNow - live.TicketCleanupRetention;

        return await database.Database
            .ExecuteSqlRawAsync(DeleteBatchSql, [cutoff, live.TicketCleanupBatchSize], cancellationToken)
            .ConfigureAwait(false);
    }

    // `expires_at` is repeated outside the subquery on purpose (the same rule as OutboxCleanupStore):
    // the subquery picks the batch from the statement's snapshot, and the outer qualifier is what
    // decides the fate of each row it named. `expires_at` never changes after the insert, so the two
    // can disagree only if a concurrent sweep already took the row — in which case the row is gone.
    private const string DeleteBatchSql =
        """
        DELETE FROM hub_tickets
        WHERE expires_at < {0}
          AND ticket_hash IN (
            SELECT ticket_hash FROM hub_tickets
            WHERE expires_at < {0}
            ORDER BY expires_at
            LIMIT {1}
          )
        """;
}
