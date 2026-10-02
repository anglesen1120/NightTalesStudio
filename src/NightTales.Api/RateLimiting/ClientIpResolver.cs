using System.Net;

namespace NightTales.Api.RateLimiting;

/// <summary>
/// The one port every partition key of the limiter resolves the caller's address through (09.3.5-T1
/// design): after <c>UseForwardedHeaders</c>, <c>Connection.RemoteIpAddress</c> is the client IP the load
/// balancer declared, or the peer's own address when the headers were not trusted.
/// </summary>
/// <remarks>
/// A port rather than a static helper so a test can pin the peer it wants without a network: the
/// address is the only input of an IP-keyed partition, so it is the seam an address test needs.
/// </remarks>
public interface IClientIpResolver
{
    /// <summary>The address the limiter partitions <paramref name="context"/> on.</summary>
    /// <param name="context">The request, after the forwarded-headers middleware ran.</param>
    IPAddress Resolve(HttpContext context);
}

/// <summary>
/// The production <see cref="IClientIpResolver"/>: the connection's remote address, which the
/// forwarded-headers middleware has already rewritten when — and only when — the peer was a trusted
/// proxy (09 README BD14).
/// </summary>
/// <remarks>
/// <para>
/// Kestrel always sets <c>Connection.RemoteIpAddress</c>, which is why the address is read and not
/// negotiated. A host without a connection feature — the in-process <c>TestServer</c> the integration
/// tests run on — reports <c>null</c>, and answers <see cref="UnknownPeer"/> instead of throwing: a
/// request must never answer 500 because its host cannot name the peer, and such requests share one
/// partition, which is exactly what an address-less caller is.
/// </para>
/// <para>
/// The resolution deliberately reads nothing else: a client-supplied header is trusted only after the
/// middleware above rewrote the connection, so no value here can be chosen by a caller outside a
/// configured proxy network (09 README BD14/D17).
/// </para>
/// </remarks>
public sealed class ClientIpResolver : IClientIpResolver
{
    /// <summary>The address a host without a connection feature reports, so every such request shares one partition.</summary>
    public static readonly IPAddress UnknownPeer = IPAddress.None;

    /// <inheritdoc />
    public IPAddress Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Connection.RemoteIpAddress ?? UnknownPeer;
    }
}
