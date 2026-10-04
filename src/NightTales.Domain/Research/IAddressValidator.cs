using System.Net;

namespace NightTales.Domain.Research;

/// <summary>
/// Decides whether the fetcher may connect to an IP address (02 D11, "global unicast only"). The production
/// implementation allows every address outside the IANA special-purpose ranges; a test double may allow loopback
/// (D20), and no production composition may register one.
/// </summary>
public interface IAddressValidator
{
    /// <summary>Whether a connection to <paramref name="address"/> is allowed.</summary>
    /// <param name="address">The resolved address.</param>
    /// <param name="reason">
    /// For a blocked address, a short code for metrics and debug logs (never shown to a user); otherwise <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> when the address may be connected to.</returns>
    bool IsAllowed(IPAddress address, out string? reason);
}
