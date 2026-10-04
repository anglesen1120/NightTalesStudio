using System.Net;
using System.Numerics;
using NightTales.Infrastructure.Research.Http;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>
/// The address rule of D11 (02.5.2-T1 §address rule): one case per CIDR of both block tables, written as
/// <see cref="TheoryData"/> built from the tables themselves so a CIDR added to a table without a case cannot slip by, plus
/// the normalisation cases and the named boundary cases.
/// </summary>
public sealed class GlobalUnicastAddressValidatorTests
{
    private static readonly GlobalUnicastAddressValidator Validator = new();

    public static TheoryData<string, string> BlockedV4 => Cases(GlobalUnicastAddressValidator.BlockedIpv4);

    public static TheoryData<string, string> BlockedV6 => Cases(GlobalUnicastAddressValidator.BlockedIpv6);

    [Theory]
    [MemberData(nameof(BlockedV4))]
    public void IsAllowed_EveryV4Cidr_FirstAndLastAddressAreBlocked(string cidr, string what)
    {
        foreach (var address in Edges(cidr))
        {
            Validator.IsAllowed(address, out var reason).ShouldBeFalse($"{address} in {cidr}");
            reason.ShouldBe(what, $"{address} in {cidr}");
        }
    }

    [Theory]
    [MemberData(nameof(BlockedV6))]
    public void IsAllowed_EveryV6Cidr_FirstAndLastAddressAreBlocked(string cidr, string what)
    {
        foreach (var address in Edges(cidr))
        {
            Validator.IsAllowed(address, out var reason).ShouldBeFalse($"{address} in {cidr}");
            reason.ShouldNotBeNull($"{address} in {cidr}");
        }

        what.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void TheTablesHoldEveryCidrOfTheSpec()
    {
        (GlobalUnicastAddressValidator.BlockedIpv4.Count + GlobalUnicastAddressValidator.BlockedIpv6.Count).ShouldBe(40);
        GlobalUnicastAddressValidator.BlockedIpv6.ShouldContain(("::ffff:0:0:0/96", "ipv4_translated"));
        GlobalUnicastAddressValidator.BlockedIpv6.ShouldContain(("100:0:0:1::/64", "ipv4v6_translation"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    public void IsAllowed_LoopbackV4_False(string address) => Validator.IsAllowed(IPAddress.Parse(address), out _).ShouldBeFalse();

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    public void IsAllowed_PrivateV4_False(string address) => Validator.IsAllowed(IPAddress.Parse(address), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_Cgnat_False() => Validator.IsAllowed(IPAddress.Parse("100.64.0.1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_LinkLocalMetadata_False() => Validator.IsAllowed(IPAddress.Parse("169.254.169.254"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_ZeroSlash8_False() => Validator.IsAllowed(IPAddress.Parse("0.0.0.0"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_Benchmarking198_18_False()
    {
        Validator.IsAllowed(IPAddress.Parse("198.18.0.1"), out _).ShouldBeFalse();
        Validator.IsAllowed(IPAddress.Parse("198.19.255.255"), out _).ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_Reserved240Slash4_False()
    {
        Validator.IsAllowed(IPAddress.Parse("240.0.0.1"), out _).ShouldBeFalse();
        Validator.IsAllowed(IPAddress.Parse("255.255.255.255"), out _).ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_Ipv4MappedLoopback_False() =>
        Validator.IsAllowed(IPAddress.Parse("::ffff:127.0.0.1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_Ipv4MappedPrivate_False() =>
        Validator.IsAllowed(IPAddress.Parse("::ffff:10.0.0.1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_Ipv4MappedPublic_True() =>
        Validator.IsAllowed(IPAddress.Parse("::ffff:93.184.216.34"), out _).ShouldBeTrue();

    [Fact]
    public void IsAllowed_Ipv4Compatible_False() => Validator.IsAllowed(IPAddress.Parse("::127.0.0.1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_6to4_False() => Validator.IsAllowed(IPAddress.Parse("2002:7f00:1::1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_Teredo_False() => Validator.IsAllowed(IPAddress.Parse("2001:0:4136:e378:8000:63bf:3fff:fdd2"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_Ula_False()
    {
        Validator.IsAllowed(IPAddress.Parse("fc00::1"), out _).ShouldBeFalse();
        Validator.IsAllowed(IPAddress.Parse("fd12:3456::1"), out _).ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_LinkLocalV6_False() => Validator.IsAllowed(IPAddress.Parse("fe80::1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_MulticastV6_False() => Validator.IsAllowed(IPAddress.Parse("ff02::1"), out _).ShouldBeFalse();

    [Fact]
    public void IsAllowed_DocumentationV6_False() => Validator.IsAllowed(IPAddress.Parse("2001:db8::1"), out _).ShouldBeFalse();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("198.20.0.1")]
    public void IsAllowed_PublicV4_True(string address)
    {
        Validator.IsAllowed(IPAddress.Parse(address), out var reason).ShouldBeTrue(address);
        reason.ShouldBeNull();
    }

    [Theory]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    [InlineData("2a00:1450:4001:81b::200e")]
    public void IsAllowed_PublicV6_True(string address) => Validator.IsAllowed(IPAddress.Parse(address), out _).ShouldBeTrue(address);

    // F3: a v6 is allowed only inside 2000::/3 and outside the table; the default-allow of the table alone is not enough.
    [Theory]
    [InlineData("4000::1")]
    [InlineData("8000::1")]
    [InlineData("c000::1")]
    [InlineData("e000::1")]
    [InlineData("::ffff:0:10.0.0.1")]
    [InlineData("100:0:0:1::1")]
    public void IsAllowed_V6OutsideGlobalUnicast_False(string address)
    {
        Validator.IsAllowed(IPAddress.Parse(address), out var reason).ShouldBeFalse(address);
        reason.ShouldBe("not_global_unicast", address);
    }

    [Theory]
    [InlineData("2000::1")]
    [InlineData("3ffe:ffff::1")]
    public void IsAllowed_V6EdgesOf2000Slash3_True(string address) =>
        Validator.IsAllowed(IPAddress.Parse(address), out _).ShouldBeTrue(address);

    [Fact]
    public void IsAllowed_ScopedV6_False()
    {
        var scoped = IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946");
        scoped.ScopeId = 3;

        Validator.IsAllowed(scoped, out var reason).ShouldBeFalse();
        reason.ShouldBe("scoped_ipv6");
    }

    private static TheoryData<string, string> Cases(IReadOnlyList<(string Cidr, string What)> table)
    {
        var data = new TheoryData<string, string>();
        foreach (var (cidr, what) in table)
        {
            data.Add(cidr, what);
        }

        return data;
    }

    // The first and last address of a CIDR block.
    private static IEnumerable<IPAddress> Edges(string cidr)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        var network = IPAddress.Parse(cidr[..slash]);
        var bits = int.Parse(cidr[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var bytes = network.GetAddressBytes();
        var total = bytes.Length * 8;

        var first = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var hostMask = (BigInteger.One << (total - bits)) - 1;
        first &= ~hostMask;
        var last = first | hostMask;

        yield return ToAddress(first, bytes.Length);
        yield return ToAddress(last, bytes.Length);
    }

    private static IPAddress ToAddress(BigInteger value, int length)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[length];
        raw.CopyTo(padded, length - raw.Length);

        return new IPAddress(padded);
    }
}
