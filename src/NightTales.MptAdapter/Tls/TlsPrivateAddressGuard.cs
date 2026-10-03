using System.Net;
using System.Net.Sockets;

namespace NightTales.MptAdapter.Tls;

/// <summary>
/// The listener's private-address guard (11 README D6, F26, §10 T44): the adapter binds the node's private
/// address only. <c>0.0.0.0</c>, <c>::</c>, hostnames and any public address are refused, and a loopback
/// address is a Development-only convenience (staging runs <c>Staging</c>, never Development).
/// </summary>
/// <remarks>The messages are fixed and never echo the configured value.</remarks>
public static class TlsPrivateAddressGuard
{
    public const string MissingMessage = "Adapter:ListenAddress is required: set it to the node's private IP address.";

    public const string AnyAddressMessage =
        "Adapter:ListenAddress must not be 0.0.0.0 or :: : the adapter binds the node's private address only.";

    public const string NotAnIpMessage = "Adapter:ListenAddress must be an IP address literal, not a hostname.";

    public const string PublicAddressMessage =
        "Adapter:ListenAddress must be a private address (10/8, 172.16/12, 192.168/16, fc00::/7); public addresses are refused.";

    public const string LoopbackMessage =
        "Adapter:ListenAddress is a loopback address, which is allowed only when ASPNETCORE_ENVIRONMENT is Development.";

    /// <summary>Null when the address may be bound; otherwise the fixed refusal message.</summary>
    public static string? Check(string? listenAddress, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(listenAddress))
        {
            return MissingMessage;
        }

        if (!IPAddress.TryParse(listenAddress.Trim('[', ']'), out var address))
        {
            return NotAnIpMessage;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return AnyAddressMessage;
        }

        if (IPAddress.IsLoopback(address))
        {
            return isDevelopment ? null : LoopbackMessage;
        }

        return IsPrivate(address) ? null : PublicAddressMessage;
    }

    /// <summary>The parsed address; call only after <see cref="Check"/> returned null.</summary>
    public static IPAddress Parse(string listenAddress)
    {
        var address = IPAddress.Parse(listenAddress.Trim('[', ']'));
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork =>
                bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168),
            AddressFamily.InterNetworkV6 => (bytes[0] & 0xFE) == 0xFC,
            _ => false,
        };
    }
}
