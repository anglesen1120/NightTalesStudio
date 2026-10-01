using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NightTales.Api.Realtime;

/// <summary>
/// The names of the groups the hub uses: one per user, <c>user:{id}</c> (09 README BD11).
/// </summary>
public static class RealtimeGroups
{
    /// <summary>The group of <paramref name="userId"/>'s open connections.</summary>
    /// <param name="userId">The owner of the connections.</param>
    public static string User(Guid userId) => $"user:{userId:D}";
}

/// <summary>
/// The hub of 09 README BD11 at <see cref="Path"/>: it accepts a ticket-authenticated socket and joins
/// it to exactly one group — the group of the user the ticket named.
/// </summary>
/// <remarks>
/// <para>
/// The scheme is the only one accepted (<c>HubTicket</c>), and the group comes from the authenticated
/// principal alone: the hub exposes no method and reads no query parameter a client could use to pick a
/// group, so a connection can never receive another user's events ([DECISION] of the task and of
/// plan §5).
/// </para>
/// <para>
/// The connection is registered in this instance's <see cref="RealtimeConnectionRegistry"/> so the
/// <c>security</c> channel (09-backend-api L218) and the backstop can abort it later. Nothing else
/// happens on connect: no database read, no stamp or status lookup ([RULED — README §B10 WT37]).
/// </para>
/// </remarks>
/// <param name="registry">This instance's registry of live connections.</param>
[Authorize(AuthenticationSchemes = HubTicketAuthenticationHandler.SchemeName)]
public sealed class EpisodeHub(RealtimeConnectionRegistry registry) : Hub
{
    /// <summary>The hub's route (BD11/09 L214); the client connects with <c>?ticket=…</c> over WebSockets.</summary>
    public const string Path = "/hubs/episodes";

    /// <summary>
    /// Joins the connection to its owner's group and registers it. A connection without a usable
    /// <c>sub</c> cannot happen behind the <c>HubTicket</c> scheme, and is refused rather than served
    /// anonymously.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        if (!HubIdentity.TryGetUserId(Context.User, out var userId))
        {
            throw new HubException("The hub connection has no subject.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(userId)).ConfigureAwait(false);
        registry.Add(userId, HubIdentity.RoleOf(Context.User), Context);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>Drops the connection's registry entry, so an instance never keeps a dead socket.</summary>
    /// <param name="exception">The error that closed the connection, when there was one.</param>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
