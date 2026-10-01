using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Realtime;

/// <summary>
/// The production <see cref="IHubTicketValidator"/> (09 README BD11, WF10; 09.8.1-T2 Design): it
/// consumes the 60-second ticket of 09.8.1-T1's <c>HubTicket</c> scheme with one atomic statement.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a single <c>UPDATE … WHERE used_at IS NULL AND expires_at &gt; @now</c>. Two
/// concurrent claims of one ticket both try to lock the row; PostgreSQL makes the loser re-check the
/// predicate after the winner commits, so exactly one sees a row and the other sees none — no
/// <c>SELECT</c>-then-<c>UPDATE</c>, no advisory lock and no Redis (BD11/WF10). A row count of zero
/// is the one answer for unknown, expired, used and tampered alike.
/// </para>
/// <para>
/// The comparison uses the injected <see cref="IClock"/>, not PostgreSQL's <c>now()</c>, so the
/// stored <c>expires_at</c> and the claim are read from the same source (D3, and the test harness's
/// <c>FakeClock</c> drives expiry without sleeping). The single-use guarantee does not depend on it:
/// only the effective TTL moves with a host's clock.
/// </para>
/// <para>
/// The winner then reads the owner and the owner's live <c>users.role</c> — the row has no role
/// column, and the hub derives the socket's role from this claim alone ([RULED — README §B10 WT37]).
/// No stamp and no status is read, on purpose: a revocation reaches the hub as the <c>security</c>
/// NOTIFY, never as a claim-time lookup (WT37, 09-backend-api L218).
/// </para>
/// <para>
/// The ticket value never reaches a log line, a metric label or an exception message (BD16/D17): a
/// failure carries only <c>hub_ticket_invalid</c>.
/// </para>
/// </remarks>
/// <param name="database">The tracked write model; its connection runs the claim.</param>
/// <param name="clock">The clock the expiry test is read from.</param>
public sealed class NpgsqlHubTicketValidator(AppDbContext database, IClock clock) : IHubTicketValidator
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<HubTicketClaim>> ClaimAsync(
        string ticket,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return Invalid();
        }

        var now = clock.UtcNow;
        var hash = HubTicketSecret.HashOf(ticket);

        var claimed = await database.Database
            .ExecuteSqlRawAsync(ClaimSql, [now, hash], cancellationToken)
            .ConfigureAwait(false);

        if (claimed != 1)
        {
            return Invalid();
        }

        // The role is read live and believed only behind a user row: the inner join also makes a user
        // deleted between the claim and this read (its tickets cascade away) answer the same 401.
        var owner = await database.HubTickets
            .AsNoTracking()
            .Where(row => row.TicketHash == hash)
            .Join(
                database.Users.AsNoTracking(),
                row => row.UserId,
                user => user.Id,
                (row, user) => new HubTicketOwner(user.Id, user.Role))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return owner is null ? Invalid() : new HubTicketClaim(owner.UserId, owner.Role);
    }

    /// <summary>
    /// Every failure mode answers this one error: a caller must not learn whether a ticket existed,
    /// expired, was used or was never issued (BD11).
    /// </summary>
    private static ErrorOr<HubTicketClaim> Invalid() =>
        Error.Unauthorized(ErrorCodes.HubTicketInvalid, "The hub ticket is invalid or was already used.");

    // `{0}` is the injected clock's reading, `{1}` the SHA-256 hash of the presented value; the
    // presented value itself is never part of the statement.
    private const string ClaimSql =
        """
        UPDATE hub_tickets SET used_at = {0}
        WHERE ticket_hash = {1} AND used_at IS NULL AND expires_at > {0}
        """;

    /// <summary>One claimed ticket's owner and role, as the read after the claim projects them.</summary>
    /// <param name="UserId">The owner of the claimed ticket.</param>
    /// <param name="Role">The owner's live platform role.</param>
    private sealed record HubTicketOwner(Guid UserId, PlatformRole Role);
}
