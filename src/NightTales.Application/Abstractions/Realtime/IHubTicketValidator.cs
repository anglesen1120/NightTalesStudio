using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// What a claimed hub ticket proves: which user it belongs to, and the platform role of that user at
/// claim time.
/// </summary>
/// <remarks>
/// The role is part of the claim because the connection cap of [RULED — README §B10 WT37] is the
/// <em>role's</em> access-token lifetime (<c>Hubs:MaxConnectionMinutes</c>: owner/admin 5, user 15),
/// and because the hub derives the socket's role from this claim alone — no stamp or status cache is
/// read anywhere on the hub path (WT37). Carrying the role costs the validator nothing: 09.8.1-T2's
/// claim already reads the user's row.
/// </remarks>
/// <param name="UserId">The owner of the claimed ticket.</param>
/// <param name="Role">That user's platform role.</param>
public sealed record HubTicketClaim(Guid UserId, PlatformRole Role);

/// <summary>
/// The port 09.8.1-T1 owns and 09.8.1-T2 implements (<c>NpgsqlHubTicketValidator</c>): the single-use
/// claim of the 60-second hub ticket of 09 README BD11.
/// </summary>
/// <remarks>
/// <para>
/// The real implementation atomically consumes the ticket with one
/// <c>UPDATE … SET used_at = now() WHERE ticket_hash = @h AND used_at IS NULL AND expires_at &gt; now()
/// RETURNING user_id</c>, so the first of two concurrent connects wins and the second sees no row
/// (BD11/WF10). Until 09.8.1-T2 lands, no production implementation is registered — the scheme fails
/// fast instead of accepting a connection (09.8.1-T1 plan §Design, "seam with 09.8.1-T2").
/// </para>
/// <para>
/// The ticket value never appears in a log line, a metric label or an exception message (09 README
/// D17), which is why the error this returns is the catalogue's constant
/// <c>hub_ticket_invalid</c> and why callers must not add the value to their own messages.
/// </para>
/// </remarks>
public interface IHubTicketValidator
{
    /// <summary>
    /// Consumes <paramref name="ticket"/> and returns its owner, or an error when the ticket is
    /// missing, unknown, tampered with, expired or already used — never both (never <c>default</c>).
    /// </summary>
    /// <param name="ticket">The value the client connected with; a bearer secret.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    ValueTask<ErrorOr<HubTicketClaim>> ClaimAsync(string ticket, CancellationToken cancellationToken);
}
