using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Realtime;

namespace NightTales.Api.Realtime;

/// <summary>
/// The live connections <em>of this Api instance</em>, so the <c>security</c> channel and the backstop
/// can close the right sockets (09.8.1 plan §5, open item 7).
/// </summary>
/// <remarks>
/// <para>
/// An in-memory map of connection id → (user, role, context, connected-at, cap) is exactly what
/// 09-backend-api L218 asks for: every instance serves its own sockets, so a revocation on instance A
/// cannot close a socket held by instance B (open item 7 accepts that residual and relies on B's own
/// backstop). Entries are removed when the hub's <c>OnDisconnectedAsync</c> runs, so an instance never
/// keeps a dead socket, and the entries back the <c>realtime_connections_active</c> gauge.
/// </para>
/// <para>
/// The cap is the connection's <em>role</em> ([RULED — README §B10 WT37]: owner/admin 5 minutes, user 15
/// — <c>Hubs:MaxConnectionMinutes</c>), which the hub read from the ticket's principal. Nothing here
/// reads a stamp, a status or any cache: the two sweeps in <see cref="AbortUser"/> and
/// <see cref="AbortExpired"/> only compare the clock with the entry.
/// </para>
/// <para>
/// The map is concurrent because the hub writes it from connection threads while the listener's loop
/// sweeps it from its own; a sweep scans the (bounded) number of connections of one instance, which is
/// at most the number of open pages.
/// </para>
/// </remarks>
/// <param name="options">The per-role caps.</param>
/// <param name="clock">The instance's clock; a test moves it instead of waiting.</param>
/// <param name="metrics">The <c>realtime_connections_active</c> gauge.</param>
public sealed class RealtimeConnectionRegistry(
    IOptions<HubsOptions> options,
    IClock clock,
    RealtimeMetrics metrics)
{
    private readonly ConcurrentDictionary<string, Registration> _connections = new(StringComparer.Ordinal);

    /// <summary>How many connections this instance currently holds.</summary>
    public int Count => _connections.Count;

    /// <summary>Registers a freshly connected socket under its owner and role.</summary>
    /// <param name="userId">The user the ticket named.</param>
    /// <param name="role">That user's role, as the ticket's principal carries it.</param>
    /// <param name="context">The SignalR connection, so a later sweep can abort it.</param>
    public void Add(Guid userId, PlatformRole role, HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cap = options.Value.CapFor(role);
        _connections[context.ConnectionId] = new Registration(userId, role, context, clock.UtcNow, cap);
        metrics.RecordActiveConnections(_connections.Count);
    }

    /// <summary>Forgets a connection that has ended.</summary>
    /// <param name="connectionId">The connection SignalR closed.</param>
    public void Remove(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);

        if (_connections.TryRemove(connectionId, out _))
        {
            metrics.RecordActiveConnections(_connections.Count);
        }
    }

    /// <summary>
    /// Aborts every connection of <paramref name="userId"/> held by this instance (the <c>security</c>
    /// channel of 09-backend-api L218) and forgets it. Other users' connections are untouched.
    /// </summary>
    /// <param name="userId">The user whose access was revoked.</param>
    /// <returns>How many connections this instance closed.</returns>
    public int AbortUser(Guid userId) => Abort(registration => registration.UserId == userId);

    /// <summary>
    /// Aborts every connection whose role's cap has elapsed ([RULED — README §B10 WT37]) and forgets it.
    /// A client whose socket is closed by this sweep fetches a new ticket and reconnects (10.2.4).
    /// </summary>
    /// <param name="now">The instant to measure the caps against.</param>
    /// <returns>How many connections this sweep closed.</returns>
    public int AbortExpired(DateTimeOffset now) =>
        Abort(registration => now - registration.ConnectedAt >= registration.Cap);

    private int Abort(Func<Registration, bool> predicate)
    {
        var aborted = 0;

        foreach (var entry in _connections)
        {
            if (!predicate(entry.Value) || !_connections.TryRemove(entry.Key, out var registration))
            {
                continue;
            }

            aborted++;
            registration.Context.Abort();
        }

        if (aborted > 0)
        {
            metrics.RecordActiveConnections(_connections.Count);
        }

        return aborted;
    }

    /// <summary>One registered socket.</summary>
    private readonly record struct Registration(
        Guid UserId,
        PlatformRole Role,
        HubCallerContext Context,
        DateTimeOffset ConnectedAt,
        TimeSpan Cap);
}
