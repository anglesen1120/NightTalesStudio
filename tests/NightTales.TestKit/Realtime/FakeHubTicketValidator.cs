using System.Collections.Concurrent;
using ErrorOr;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.TestKit.Realtime;

/// <summary>
/// The hand-written stand-in for 09.8.1-T2's <c>NpgsqlHubTicketValidator</c> (09 README D13: no mocking
/// library), so the hub, its group membership and its fan-out are testable before the
/// <c>hub_tickets</c> table exists.
/// </summary>
/// <remarks>
/// <para>
/// It reproduces the one behaviour the hub depends on: a ticket is <em>issued</em> to one user and role,
/// and <em>claiming</em> it consumes it — a second claim of the same value fails with the catalogue's
/// <c>hub_ticket_invalid</c>, exactly as T2's atomic <c>UPDATE … WHERE used_at IS NULL</c> will. The
/// expiry, the hash-at-rest rule and the concurrency of the real claim are T2's tests, not this fake's.
/// </para>
/// <para>
/// The value is random per issue, so a test can prove that no log line, metric or body carries it: any
/// occurrence of <see cref="Issue"/>'s return value is a leak into a place a ticket must never reach.
/// </para>
/// </remarks>
public sealed class FakeHubTicketValidator : IHubTicketValidator
{
    private readonly ConcurrentDictionary<string, HubTicketClaim> _issued = new(StringComparer.Ordinal);
    private int _claimed;
    private int _issuedCount;

    /// <summary>How many tickets were issued and not yet claimed.</summary>
    public int OutstandingCount => _issued.Count;

    /// <summary>How many claims succeeded (the tickets consumed so far).</summary>
    public int ClaimedCount => Volatile.Read(ref _claimed);

    /// <summary>Issues a single-use ticket for <paramref name="userId"/> as <paramref name="role"/>.</summary>
    /// <param name="userId">The user the ticket will authenticate.</param>
    /// <param name="role">The role the claim will carry (owner/admin 5 minutes, user 15 — WT37).</param>
    public string Issue(Guid userId, PlatformRole role = PlatformRole.User)
    {
        var ticket = $"nts-ticket-{Interlocked.Increment(ref _issuedCount):D4}-{Guid.CreateVersion7():N}";
        _issued[ticket] = new HubTicketClaim(userId, role);

        return ticket;
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<HubTicketClaim>> ClaimAsync(string ticket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket) || !_issued.TryRemove(ticket, out var claim))
        {
            return ValueTask.FromResult<ErrorOr<HubTicketClaim>>(
                Error.Unauthorized(
                    ErrorCodes.HubTicketInvalid,
                    "The hub ticket is missing, expired, used or invalid."));
        }

        Interlocked.Increment(ref _claimed);

        return ValueTask.FromResult<ErrorOr<HubTicketClaim>>(claim);
    }
}
