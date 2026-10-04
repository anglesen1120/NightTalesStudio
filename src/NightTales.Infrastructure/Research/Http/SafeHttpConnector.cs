using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// The anti-rebinding core of the fetcher (02 D11, F14; 02.5.2-T1 §ConnectAsync): the <c>ConnectCallback</c> of the
/// handler. <b>The control is resolve-then-connect-to-IP</b>: the name is resolved here, every address is validated, and
/// the socket is connected to that <see cref="IPAddress"/> — <see cref="HttpClient"/> never resolves the host again, so
/// the address that was checked is the address connected to.
/// </summary>
/// <remarks>
/// The optional <c>endpointFor</c> maps a validated address and the requested port to the endpoint to dial. Production
/// passes none (the endpoint is the validated address and the requested port); the loopback test server uses it to
/// listen on a random port while the URL keeps the only ports a source URL may have, 80 and 443. It runs after the
/// address check and is not checked by <see cref="IAddressValidator"/>: it can return any endpoint, so production code
/// must never pass one (D20). The hook is public only because this solution has no <c>InternalsVisibleTo</c>.
/// </remarks>
public sealed partial class SafeHttpConnector(
    IDnsResolver dns,
    IAddressValidator addresses,
    ILogger<SafeHttpConnector> log,
    Func<IPAddress, int, IPEndPoint>? endpointFor = null)
{
    /// <summary>Connects to <see cref="SocketsHttpConnectionContext.DnsEndPoint"/> through the address rules.</summary>
    /// <param name="ctx">The connection context of the handler.</param>
    /// <param name="ct">Cancels the connect.</param>
    /// <returns>The connected stream.</returns>
    /// <exception cref="SourceFetchException">The port, host or addresses are not allowed or none connects.</exception>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        if (port is not (80 or 443))
        {
            throw new SourceFetchException(SourceErrors.SourceUrlInvalid);
        }

        // Belt and braces: SourceUrl already rejected IP literals.
        if (IPAddress.TryParse(host, out _))
        {
            throw new SourceFetchException(SourceErrors.SourceUrlInvalid);
        }

        var resolved = await dns.ResolveAsync(host, ct).ConfigureAwait(false);
        if (resolved.Count == 0)
        {
            throw new SourceFetchException(SourceErrors.SourceUnreachable);
        }

        // A new Socket per attempt: on Linux a socket cannot be reused after a failed connect.
        Socket? socket = null;
        try
        {
            foreach (var ip in resolved)
            {
                if (!addresses.IsAllowed(ip, out var reason))
                {
                    LogAddressBlocked(reason);
                    continue;
                }

                var attempt = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var endpoint = endpointFor is null ? new IPEndPoint(ip, port) : endpointFor(ip, port);
                    await attempt.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    socket = attempt;
                    break;
                }
                catch (SocketException)
                {
                    attempt.Dispose();
                }
                catch
                {
                    attempt.Dispose();
                    throw;
                }
            }

            if (socket is null)
            {
                throw new SourceFetchException(SourceErrors.SourceUnreachable);
            }

            // Defence in depth only: the address connected to is already the validated one (we resolved here and never
            // let HttpClient re-resolve), so this can only fire if the socket API returns a peer that differs from the
            // endpoint we asked for. It is not the rebinding control.
            if (socket.RemoteEndPoint is IPEndPoint peer && !addresses.IsAllowed(peer.Address, out _))
            {
                throw new SourceFetchException(SourceErrors.SourceUrlInvalid);
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "fetch address blocked: {Reason}")]
    private partial void LogAddressBlocked(string? reason);
}
