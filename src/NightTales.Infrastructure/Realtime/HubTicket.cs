namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// One issued hub ticket (09 README BD11, 09.8.1-T2 Design): the <c>SHA-256</c> hex of a 60-second,
/// single-use ticket is the row's primary key, and the ticket itself is never stored.
/// </summary>
/// <remarks>
/// <para>
/// Infrastructure-only, like <see cref="Persistence.Entities.RefreshToken"/> and
/// <see cref="Persistence.Outbox.OutboxMessage"/>: nothing in Application names this type, and the
/// Application side speaks through <c>IHubTicketStore</c> and <c>IHubTicketValidator</c> instead.
/// </para>
/// <para>
/// It deliberately does <em>not</em> implement <c>IUserOwned</c>: a claim runs for a caller who has
/// no principal yet (the ticket is what authenticates them), so a tenant filter on this table would
/// make the claim's lookup read no row at all. The claim is also the only mutation of an existing
/// row, and it is a set-based <c>UPDATE</c> in <see cref="NpgsqlHubTicketValidator"/> — this type
/// only ever inserts.
/// </para>
/// <para>
/// No <c>xmin</c>: the table is not an aggregate root, so <c>AggregateRootConvention</c> leaves its
/// model alone (09.8.1-T2 Design).
/// </para>
/// </remarks>
public sealed class HubTicket
{
    private HubTicket()
    {
    }

    /// <summary>The lowercase <c>SHA-256</c> hex of the ticket; the primary key and the only lookup key.</summary>
    public string TicketHash { get; private set; } = string.Empty;

    /// <summary>The owning user; <c>ON DELETE CASCADE</c>.</summary>
    public Guid UserId { get; private set; }

    /// <summary>When the row was written, from the scope's clock.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the ticket stops being claimable: <see cref="CreatedAt"/> plus the configured TTL.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the ticket was claimed, or null while it is still claimable. Never reset.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Creates the row of one issued ticket; the caller adds it to the unit of work.</summary>
    /// <param name="ticketHash">The hash handed to the client, as <c>Sha256Hex</c> produced it.</param>
    /// <param name="userId">The owner the ticket will authenticate.</param>
    /// <param name="createdAt">The instant of the issue, from <c>IClock</c>.</param>
    /// <param name="expiresAt">The instant the ticket stops being claimable.</param>
    public static HubTicket Create(string ticketHash, Guid userId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
        => new()
        {
            TicketHash = ticketHash,
            UserId = userId,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        };
}
