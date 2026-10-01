using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NightTales.Api.Security;

namespace NightTales.Api.RateLimiting;

/// <summary>
/// The partition keys of the four policies (09.3.5-T1 design, 09 README BD14/BD16): an IP-keyed policy
/// counts a hash of the resolved address, a user-keyed one counts a hash of the authenticated
/// <c>sub</c> or — for a caller without one — the hash of the resolved address under a different prefix.
/// </summary>
/// <remarks>
/// <para>
/// The address and the user id are hashed, never used verbatim, because the key is not only the
/// limiter's: it is also the identifier a rejection logs (BD16: no IP and no user id in logs). The digest
/// is truncated to <see cref="HashHexLength"/> hex characters, which keeps a key short while leaving a
/// collision irrelevant: the hash only has to separate buckets of one window, and two colliding values
/// would merely share a limit.
/// </para>
/// <para>
/// The prefixes make the kinds distinguishable in a metric or a log line without a second field, and
/// keep a user id from ever colliding with an address hash.
/// </para>
/// <para>
/// An IP-keyed key keeps only the IPv6 prefix of the address (<see cref="RateLimitingOptions.Ipv6PrefixLength"/>,
/// default /64). An IPv6 subscriber controls a whole /64, so keying all 128 bits would hand a caller that
/// rotates its interface address a fresh budget per address — the same evasion a forged forwarded header
/// would give (SEC-0935-02). IPv4-mapped IPv6 addresses still collapse to the IPv4 address they carry,
/// so one client arriving over either family shares one bucket (BD16).
/// </para>
/// <para>
/// The user-keyed <c>anon:</c> fallback exists because a user-keyed policy may still see a request with
/// no usable <c>sub</c> — the limiter runs for every request through the middleware chain, whatever the
/// endpoint later answers. Falling back to one shared bucket would let an unauthenticated caller exhaust
/// every other anonymous caller's budget, so the fallback is per address, exactly like the IP-keyed
/// policies (09.3.5-T1 Tests).
/// </para>
/// </remarks>
public static class RateLimitPartitionKeys
{
    /// <summary>The prefix of a partition keyed by a hashed address.</summary>
    public const string IpPrefix = "ip:";

    /// <summary>The prefix of a partition keyed by the authenticated user id.</summary>
    public const string UserPrefix = "user:";

    /// <summary>The prefix of the fallback partition of a user-keyed policy whose caller has no <c>sub</c>.</summary>
    public const string AnonymousPrefix = "anon:";

    /// <summary>How many leading hex characters of the SHA-256 digest a key keeps.</summary>
    public const int HashHexLength = 32;

    /// <summary>The key under which the partition key the limiter used is parked on the request.</summary>
    /// <remarks>
    /// The rejection callback logs the key the limiter already computed instead of hashing the address a
    /// second time on the rejection path; it reads this item and falls back to a fresh computation when
    /// it is absent (09.3.5-T1 design).
    /// </remarks>
    internal const string PartitionKeyItemKey = "NightTales.RateLimiting.PartitionKey";

    /// <summary>The whole SHA-256 digest, the only length <c>SHA256.HashData</c> writes.</summary>
    private const int DigestBytes = 32;

    /// <summary>How many digest bytes a key formats: <see cref="HashHexLength"/> hex characters.</summary>
    private const int KeyDigestBytes = HashHexLength / 2;

    /// <summary>The longest subject hashed on the stack before the hasher rents a pooled buffer.</summary>
    private const int StackSubjectByteLimit = 256;

    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// The partition key of <paramref name="policy"/> for <paramref name="context"/>: the hashed address
    /// for the IP-keyed policies and the user (or the address fallback) for the user-keyed ones.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="ipResolver">The resolver the IP half uses.</param>
    /// <param name="policy">A name of <see cref="RateLimitPolicies.All"/>.</param>
    /// <param name="ipv6PrefixLength">The IPv6 prefix an address key keeps.</param>
    public static string For(HttpContext context, IClientIpResolver ipResolver, string policy, int ipv6PrefixLength) =>
        RateLimitPolicies.IsIpKeyed(policy)
            ? Ip(context, ipResolver, ipv6PrefixLength)
            : User(context, ipResolver, ipv6PrefixLength);

    /// <summary>The partition key of an IP-keyed policy: <c>ip:&lt;sha256-hex of the resolved address&gt;</c>.</summary>
    /// <param name="context">The request, after the forwarded-headers middleware ran.</param>
    /// <param name="ipResolver">The resolver of the caller's address.</param>
    /// <param name="ipv6PrefixLength">The IPv6 prefix an address key keeps.</param>
    public static string Ip(HttpContext context, IClientIpResolver ipResolver, int ipv6PrefixLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ipResolver);

        Span<byte> digest = stackalloc byte[DigestBytes];
        AddressDigest(ipResolver.Resolve(context), ipv6PrefixLength, digest);

        return PrefixedKey(IpPrefix, digest);
    }

    /// <summary>
    /// The partition key of a user-keyed policy: <c>user:&lt;sha256-hex of the sub&gt;</c>, or
    /// <c>anon:&lt;sha256-hex of the resolved address&gt;</c> when the request carries no usable
    /// <c>sub</c>.
    /// </summary>
    /// <param name="context">The request, after authentication ran.</param>
    /// <param name="ipResolver">The resolver the fallback uses.</param>
    /// <param name="ipv6PrefixLength">The IPv6 prefix an address key keeps.</param>
    public static string User(HttpContext context, IClientIpResolver ipResolver, int ipv6PrefixLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ipResolver);

        Span<byte> digest = stackalloc byte[DigestBytes];
        if (context.User.FindFirst(HttpCurrentUser.SubjectClaimType)?.Value is { Length: > 0 } sub)
        {
            SubjectDigest(sub, digest);

            return PrefixedKey(UserPrefix, digest);
        }

        AddressDigest(ipResolver.Resolve(context), ipv6PrefixLength, digest);

        return PrefixedKey(AnonymousPrefix, digest);
    }

    /// <summary>
    /// The truncated lowercase SHA-256 hex digest of one address, as a key uses it: the address is
    /// canonicalised first, so an IPv4-mapped IPv6 address hashes exactly like the IPv4 address it
    /// carries, and a native IPv6 address keeps only its <paramref name="ipv6PrefixLength"/> prefix.
    /// </summary>
    /// <param name="address">The resolved address.</param>
    /// <param name="ipv6PrefixLength">The IPv6 prefix a native IPv6 address keeps; ignored for IPv4.</param>
    /// <remarks>
    /// The normalisation is the whole point of routing every address key through here: a client that
    /// arrives as <c>::ffff:203.0.113.40</c> on one connection and <c>203.0.113.40</c> on the next is one
    /// caller, and two keys would hand it a second, untouched budget (09 README BD16). The prefix does
    /// the same for the addresses one IPv6 subscriber controls (SEC-0935-02).
    /// </remarks>
    public static string Hash(IPAddress address, int ipv6PrefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        Span<byte> digest = stackalloc byte[DigestBytes];
        AddressDigest(address, ipv6PrefixLength, digest);

        return Hex(digest);
    }

    /// <summary>
    /// The truncated lowercase SHA-256 hex digest of one user id, as a key uses it: the key is what a
    /// rejection logs, so the id itself never reaches a log line (BD16).
    /// </summary>
    /// <param name="value">The authenticated subject.</param>
    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Span<byte> digest = stackalloc byte[DigestBytes];
        SubjectDigest(value, digest);

        return Hex(digest);
    }

    /// <summary>Hashes one address into <paramref name="destination"/>, canonicalising and prefixing it first.</summary>
    private static void AddressDigest(IPAddress address, int ipv6PrefixLength, Span<byte> destination)
    {
        var canonical = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (canonical.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            canonical.TryWriteBytes(bytes, out _);
            MaskIpv6Prefix(bytes, ipv6PrefixLength);
            SHA256.HashData(bytes, destination);

            return;
        }

        Span<byte> v4 = stackalloc byte[4];
        canonical.TryWriteBytes(v4, out _);
        SHA256.HashData(v4, destination);
    }

    /// <summary>Hashes one subject into <paramref name="destination"/> without copying it into an array.</summary>
    private static void SubjectDigest(string value, Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= StackSubjectByteLimit)
        {
            Span<byte> bytes = stackalloc byte[StackSubjectByteLimit];
            var written = Encoding.UTF8.GetBytes(value, bytes);
            SHA256.HashData(bytes[..written], destination);

            return;
        }

        // A subject longer than the stack bound visits a pooled buffer instead of growing the frame; a
        // `sub` is a ULID-sized string in practice, so this path is the guard, not the hot path.
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, rented);
            SHA256.HashData(rented.AsSpan(0, written), destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Clears every bit of a 16-byte IPv6 address after <paramref name="ipv6PrefixLength"/>.</summary>
    private static void MaskIpv6Prefix(Span<byte> bytes, int ipv6PrefixLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ipv6PrefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ipv6PrefixLength, 128);

        var wholeBytes = ipv6PrefixLength >> 3;
        var remainingBits = ipv6PrefixLength & 7;
        if (remainingBits != 0)
        {
            bytes[wholeBytes] &= (byte)(0xFF << (8 - remainingBits));
            wholeBytes++;
        }

        bytes[wholeBytes..].Clear();
    }

    /// <summary>Formats the key digest into <paramref name="prefix"/> as one string, with no intermediate hex.</summary>
    private static string PrefixedKey(string prefix, ReadOnlySpan<byte> digest)
    {
        Span<char> key = stackalloc char[prefix.Length + HashHexLength];
        prefix.CopyTo(key);
        WriteHex(digest, key[prefix.Length..]);

        return new string(key);
    }

    /// <summary>The bare truncated hex digest, the form <see cref="Hash(IPAddress, int)"/> and <see cref="Hash(string)"/> answer.</summary>
    private static string Hex(ReadOnlySpan<byte> digest)
    {
        Span<char> hex = stackalloc char[HashHexLength];
        WriteHex(digest, hex);

        return new string(hex);
    }

    /// <summary>Writes the first <see cref="KeyDigestBytes"/> digest bytes as lowercase hex into <paramref name="destination"/>.</summary>
    private static void WriteHex(ReadOnlySpan<byte> digest, Span<char> destination)
    {
        for (var i = 0; i < KeyDigestBytes; i++)
        {
            destination[i * 2] = HexDigits[digest[i] >> 4];
            destination[(i * 2) + 1] = HexDigits[digest[i] & 0x0F];
        }
    }
}
