using System.Net;
using System.Net.Sockets;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// The production <see cref="IAddressValidator"/> (02 D11 "global unicast only", F14; 02.5.2-T1 Design): normalise the
/// address, then allow an IPv4 address that is in no IPv4 block row, and an IPv6 address only when it is inside
/// <c>2000::/3</c> (the global unicast block) and in no IPv6 block row. Any other IPv6 address is rejected before the table
/// is consulted. There is no loopback or private-network bypass here
/// (D20): the test double lives in <c>NightTales.TestKit</c>.
/// </summary>
/// <remarks>
/// Normalisation: an IPv4-mapped IPv6 address is judged as its IPv4 address; an address in the deprecated
/// IPv4-compatible range <c>::/96</c> is rejected; a scoped IPv6 address is rejected; 6to4 (<c>2002::/16</c>) and
/// Teredo (<c>2001::/32</c>) are rejected as ranges and never unwrapped.
/// </remarks>
public sealed class GlobalUnicastAddressValidator : IAddressValidator
{
    /// <summary>The IPv4 block table, as CIDR text with a short description (02.5.2-T1 §address rule).</summary>
    public static IReadOnlyList<(string Cidr, string What)> BlockedIpv4 { get; } =
    [
        ("0.0.0.0/8", "this_network"),
        ("10.0.0.0/8", "rfc1918"),
        ("100.64.0.0/10", "cgnat"),
        ("127.0.0.0/8", "loopback"),
        ("169.254.0.0/16", "link_local"),
        ("172.16.0.0/12", "rfc1918"),
        ("192.0.0.0/24", "ietf_protocol_assignments"),
        ("192.0.2.0/24", "test_net_1"),
        ("192.88.99.0/24", "6to4_relay_anycast"),
        ("192.168.0.0/16", "rfc1918"),
        ("198.18.0.0/15", "benchmarking"),
        ("198.51.100.0/24", "test_net_2"),
        ("203.0.113.0/24", "test_net_3"),
        ("224.0.0.0/4", "multicast"),
        ("240.0.0.0/4", "reserved"),
    ];

    /// <summary>The IPv6 block table, as CIDR text with a short description (02.5.2-T1 §address rule).</summary>
    public static IReadOnlyList<(string Cidr, string What)> BlockedIpv6 { get; } =
    [
        ("::/128", "unspecified"),
        ("::1/128", "loopback"),
        ("::/96", "ipv4_compatible"),
        ("::ffff:0:0:0/96", "ipv4_translated"),
        ("64:ff9b::/96", "nat64"),
        ("64:ff9b:1::/48", "nat64_local"),
        ("100::/64", "discard_only"),
        ("100:0:0:1::/64", "ipv4v6_translation"),
        ("2001::/32", "teredo"),
        ("2001:1::1/128", "protocol_assignment"),
        ("2001:1::2/128", "protocol_assignment"),
        ("2001:2::/48", "benchmarking"),
        ("2001:3::/32", "amt"),
        ("2001:4:112::/48", "as112"),
        ("2001:20::/28", "orchidv2"),
        ("2001:30::/28", "drone_remote_id"),
        ("2001:db8::/32", "documentation"),
        ("2002::/16", "6to4"),
        ("2620:4f:8000::/48", "as112_direct"),
        ("3fff::/20", "documentation"),
        ("5f00::/16", "segment_routing"),
        ("fc00::/7", "ula"),
        ("fe80::/10", "link_local"),
        ("fec0::/10", "site_local"),
        ("ff00::/8", "multicast"),
    ];

    private static readonly Block[] V4Blocks = Compile(BlockedIpv4);

    private static readonly Block[] V6Blocks = Compile(BlockedIpv6);

    /// <inheritdoc />
    public bool IsAllowed(IPAddress address, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            else if (address.ScopeId != 0)
            {
                reason = "scoped_ipv6";
                return false;
            }
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            reason = "unreadable_address";
            return false;
        }

        // Positive check first (02.5.2-T1 §address rule): a v6 is a global unicast candidate only inside 2000::/3.
        // Anything else is rejected here and never reaches the table; the table then catches the bad ranges inside
        // 2000::/3 and keeps the full IANA list for auditability. An IPv4-mapped address was unwrapped above and is
        // judged as its IPv4 address, so it never meets this check.
        if (written == 16 && (bytes[0] & 0xE0) != 0x20)
        {
            reason = "not_global_unicast";
            return false;
        }

        reason = Find(written == 4 ? V4Blocks : V6Blocks, bytes[..written]);

        return reason is null;
    }

    private static string? Find(Block[] blocks, ReadOnlySpan<byte> address)
    {
        foreach (var block in blocks)
        {
            if (block.Contains(address))
            {
                return block.What;
            }
        }

        return null;
    }

    private static Block[] Compile(IReadOnlyList<(string Cidr, string What)> table)
    {
        var blocks = new Block[table.Count];
        for (var index = 0; index < table.Count; index++)
        {
            var (cidr, what) = table[index];
            var slash = cidr.IndexOf('/', StringComparison.Ordinal);
            var network = IPAddress.Parse(cidr.AsSpan(0, slash));
            var bits = int.Parse(cidr.AsSpan(slash + 1), System.Globalization.CultureInfo.InvariantCulture);
            blocks[index] = new Block(network.GetAddressBytes(), bits, what);
        }

        return blocks;
    }

    private readonly record struct Block(byte[] Network, int Bits, string What)
    {
        public bool Contains(ReadOnlySpan<byte> address)
        {
            var whole = Bits / 8;
            if (!address[..whole].SequenceEqual(Network.AsSpan(0, whole)))
            {
                return false;
            }

            var rest = Bits % 8;
            if (rest == 0)
            {
                return true;
            }

            var mask = (byte)(0xFF << (8 - rest));

            return (address[whole] & mask) == (Network[whole] & mask);
        }
    }
}
