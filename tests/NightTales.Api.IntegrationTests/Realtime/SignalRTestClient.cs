using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using NightTales.Api.Realtime;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Realtime;

/// <summary>
/// A hand-written SignalR client over the host's in-memory transport (09 README D13: no mocking library
/// and no client package — every frame the server sends is asserted here instead of being interpreted by
/// a client library), speaking the documented JSON protocol: one JSON message per frame, terminated by
/// <c>0x1e</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConnectAsync"/> completes the handshake <em>and</em> proves the hub's
/// <c>OnConnectedAsync</c> ran: the client invokes a method the hub does not have, and the error
/// completion for it can only arrive once the receive loop is live — i.e. after the group join. That is
/// what makes every delivery assertion in these tests deterministic without a single sleep.
/// </para>
/// <para>
/// Server pings (type 6) are skipped, so a test reads the invocations in the order the server sent them.
/// </para>
/// </remarks>
internal sealed class SignalRTestClient : IAsyncDisposable
{
    /// <summary>SignalR terminates every frame with the ASCII record separator.</summary>
    private const byte RecordSeparator = 0x1e;

    /// <summary>The handshake the client opens with; the server answers an empty object.</summary>
    private const string HandshakeFrame = """{"protocol":"json","version":1}""" + "\u001e";

    /// <summary>A probe method the hub does not expose; its error completion proves the receive loop is live.</summary>
    private const string ProbeFrame = """{"type":1,"invocationId":"nts-probe","target":"nts_probe","arguments":[]}""" + "\u001e";

    private readonly byte[] _chunk = new byte[8 * 1024];
    private readonly List<byte> _pending = [];
    private readonly WebSocket _socket;

    private SignalRTestClient(WebSocket socket) => _socket = socket;

    /// <summary>
    /// Opens the socket at <paramref name="path"/> with the ticket in the query string, completes the
    /// handshake and waits until the hub has finished <c>OnConnectedAsync</c>.
    /// </summary>
    /// <param name="server">The TestServer of the host under test.</param>
    /// <param name="ticket">The single-use ticket; it is consumed by this connect.</param>
    /// <param name="extraQuery">
    /// Any extra query parameters a hostile client might add to steer the group — they must be ignored.
    /// </param>
    /// <param name="cancellationToken">The test's bound.</param>
    public static async Task<SignalRTestClient> ConnectAsync(
        TestServer server,
        string ticket,
        string? extraQuery,
        CancellationToken cancellationToken)
    {
        var path = EpisodeHub.Path
            + "?" + HubTicketAuthenticationHandler.TicketParameterName + "=" + Uri.EscapeDataString(ticket)
            + (string.IsNullOrEmpty(extraQuery) ? string.Empty : "&" + extraQuery);

        var socket = await server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost" + path), cancellationToken);

        var client = new SignalRTestClient(socket);

        await client.HandshakeAsync(cancellationToken);
        await client.AwaitConnectedAsync(cancellationToken);

        return client;
    }

    /// <summary>
    /// The next invocation the server sent: the group's target (the event's method name) and its first
    /// argument.
    /// </summary>
    public async Task<HubInvocation> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var message = await ReadFrameAsync(cancellationToken);

            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 1)
            {
                // A ping (6) or a completion (3) is not a delivery.
                continue;
            }

            return new HubInvocation(
                root.GetProperty("target").GetString()!,
                root.GetProperty("arguments")[0].Clone());
        }
    }

    /// <summary>
    /// Waits until the server closes the socket — what an abort looks like from this side (a close frame,
    /// or a broken transport).
    /// </summary>
    /// <param name="cancellationToken">The test's bound; a socket that stays open cancels this call.</param>
    public async Task<bool> ClosedAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(_chunk.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return true;
                }
            }
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            // An aborted in-memory transport can also answer "the socket is not connected"; both shapes
            // mean the same thing to these tests.
            return true;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Disposing an IAsyncDisposable socket aborts it, which is exactly what a test's teardown wants:
        // the server sees the disconnect either way.
        _socket.Dispose();

        return ValueTask.CompletedTask;
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await SendAsync(HandshakeFrame, cancellationToken);

        using var response = await ReadFrameAsync(cancellationToken);
        response.RootElement.GetRawText().ShouldBe("{}", "an accepted handshake answers an empty object");
    }

    private async Task AwaitConnectedAsync(CancellationToken cancellationToken)
    {
        await SendAsync(ProbeFrame, cancellationToken);

        while (true)
        {
            using var message = await ReadFrameAsync(cancellationToken);

            if (!message.RootElement.TryGetProperty("invocationId", out var id)
                || !string.Equals(id.GetString(), "nts-probe", StringComparison.Ordinal))
            {
                continue;
            }

            // The hub exposes no invocable method, so the probe is answered with an error completion —
            // and it can only be answered at all once OnConnectedAsync has returned.
            message.RootElement.GetProperty("type").GetInt32().ShouldBe(3);
            message.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();

            return;
        }
    }

    private async Task SendAsync(string frame, CancellationToken cancellationToken) =>
        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(frame).AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

    /// <summary>Reads one RS-terminated frame, buffering whatever a fragmented receive leaves behind.</summary>
    private async Task<JsonDocument> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var terminator = _pending.IndexOf(RecordSeparator);
            if (terminator >= 0)
            {
                var frame = Encoding.UTF8.GetString([.. _pending.GetRange(0, terminator)]);
                _pending.RemoveRange(0, terminator + 1);

                return JsonDocument.Parse(frame);
            }

            var result = await _socket.ReceiveAsync(_chunk.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The hub closed the connection before answering.");
            }

            _pending.AddRange(_chunk.AsSpan(0, result.Count).ToArray());
        }
    }

    /// <summary>One invocation of the JSON protocol: the method the server called and its first argument.</summary>
    /// <param name="Target">The SignalR method name, i.e. the event's PascalCase name.</param>
    /// <param name="Argument">The forwarded NOTIFY body.</param>
    internal readonly record struct HubInvocation(string Target, JsonElement Argument)
    {
        /// <summary>The wire event member of the forwarded body.</summary>
        public string? Event => Argument.TryGetProperty("event", out var value) ? value.GetString() : null;

        /// <summary>The <c>ids</c> member of the forwarded body as a raw element.</summary>
        public JsonElement Ids => Argument.TryGetProperty("ids", out var ids) ? ids : default;

        /// <summary>One id of the forwarded body, or <c>null</c> when the member is absent or null.</summary>
        /// <param name="member">The camelCase member name (<c>episodeId</c>, …).</param>
        public Guid? IdOf(string member) =>
            Ids.ValueKind == JsonValueKind.Object
            && Ids.TryGetProperty(member, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetGuid()
                : null;
    }
}
