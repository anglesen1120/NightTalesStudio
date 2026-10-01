using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NightTales.Application.Abstractions.Realtime;

namespace NightTales.Api.Realtime;

/// <summary>
/// The one thing the listener does with a <c>realtime</c> message: hand it to the local connections of
/// one user (09 README BD11: every instance forwards to <em>its own</em> group members only).
/// </summary>
/// <remarks>
/// The seam exists so the loop can be driven without a server (its reconnect, backstop and
/// unknown-event behaviour are asserted with hand-written fakes, 09 README D13); the production
/// implementation is <see cref="SignalRRealtimeGroupSender"/> over the real <c>IHubContext</c>, and the
/// end-to-end tests exercise that one through a real socket.
/// </remarks>
public interface IRealtimeGroupSender
{
    /// <summary>
    /// Sends the event to the connections of <paramref name="userId"/> held by this instance; a user
    /// with no connection here makes this a no-op (SignalR's group is local to the instance).
    /// </summary>
    /// <param name="userId">The user whose group receives the event.</param>
    /// <param name="name">The event, whose PascalCase name is the client-facing method.</param>
    /// <param name="body">The NOTIFY body, forwarded unchanged.</param>
    /// <param name="cancellationToken">The loop's token.</param>
    Task SendAsync(Guid userId, RealtimeEventName name, JsonElement body, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IRealtimeGroupSender"/> over the host's own SignalR hub context.
/// </summary>
/// <param name="hub">The context of <see cref="EpisodeHub"/>, whose groups are this instance's sockets.</param>
public sealed class SignalRRealtimeGroupSender(IHubContext<EpisodeHub> hub) : IRealtimeGroupSender
{
    /// <inheritdoc />
    public Task SendAsync(Guid userId, RealtimeEventName name, JsonElement body, CancellationToken cancellationToken) =>
        hub.Clients
            .Group(RealtimeGroups.User(userId))
            .SendAsync(name.ToMethodName(), body, cancellationToken);
}
