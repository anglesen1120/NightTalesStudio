using System.Net;
using System.Net.Sockets;
using NightTales.Domain.Research;

namespace NightTales.Fetcher.Core.Research.Http;

/// <summary>
/// The production <see cref="IDnsResolver"/>: the operating system's resolver through <see cref="Dns"/>. A name that does
/// not resolve yields an empty list, which the connect callback turns into <c>source_unreachable</c>.
/// </summary>
public sealed class SocketDnsResolver : IDnsResolver
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);

        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
