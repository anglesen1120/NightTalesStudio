using System.Net;
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
/// limiter's: it is also the identifier a rejection logs (BD16: no IP and no user id in logs). The hash
/// is truncated to <see cref="HashHexLength"/> hex characters, which keeps a key short while leaving a
/// collision irrelevant: the hash only has to separate buckets of one window, and two colliding values
/// would merely share a limit.
/// </para>
/// <para>
/// The prefixes make the kinds distinguishable in a metric or a log line without a second field, and
/// keep a user id from ever colliding with an address hash.
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

    /// <summary>
    /// The partition key of <paramref name="policy"/> for <paramref name="context"/>: the hashed address
    /// for the IP-keyed policies and the user (or the address fallback) for the user-keyed ones.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="ipResolver">The resolver the IP half uses.</param>
    /// <param name="policy">A name of <see cref="RateLimitPolicies.All"/>.</param>
    public static string For(HttpContext context, IClientIpResolver ipResolver, string policy) =>
        RateLimitPolicies.IsIpKeyed(policy) ? Ip(context, ipResolver) : User(context, ipResolver);

    /// <summary>The partition key of an IP-keyed policy: <c>ip:&lt;sha256-hex of the resolved address&gt;</c>.</summary>
    /// <param name="context">The request, after the forwarded-headers middleware ran.</param>
    /// <param name="ipResolver">The resolver of the caller's address.</param>
    public static string Ip(HttpContext context, IClientIpResolver ipResolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ipResolver);

        return IpPrefix + Hash(ipResolver.Resolve(context));
    }

    /// <summary>
    /// The partition key of a user-keyed policy: <c>user:&lt;sha256-hex of the sub&gt;</c>, or
    /// <c>anon:&lt;sha256-hex of the resolved address&gt;</c> when the request carries no usable
    /// <c>sub</c>.
    /// </summary>
    /// <param name="context">The request, after authentication ran.</param>
    /// <param name="ipResolver">The resolver the fallback uses.</param>
    public static string User(HttpContext context, IClientIpResolver ipResolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ipResolver);

        return context.User.FindFirst(HttpCurrentUser.SubjectClaimType)?.Value is { Length: > 0 } sub
            ? UserPrefix + Hash(sub)
            : AnonymousPrefix + Hash(ipResolver.Resolve(context));
    }

    /// <summary>
    /// The truncated lowercase SHA-256 hex digest of one address, as a key uses it: the address is
    /// canonicalised first, so an IPv4-mapped IPv6 address hashes exactly like the IPv4 address it
    /// carries.
    /// </summary>
    /// <param name="address">The resolved address.</param>
    /// <remarks>
    /// The normalisation is the whole point of routing every address key through here: a client that
    /// arrives as <c>::ffff:203.0.113.40</c> on one connection and <c>203.0.113.40</c> on the next is one
    /// caller, and two keys would hand it a second, untouched budget (09 README BD16).
    /// </remarks>
    public static string Hash(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var canonical = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return Hash(canonical.GetAddressBytes());
    }

    /// <summary>
    /// The truncated lowercase SHA-256 hex digest of one user id, as a key uses it: the key is what a
    /// rejection logs, so the id itself never reaches a log line (BD16).
    /// </summary>
    /// <param name="value">The authenticated subject.</param>
    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Hash(Encoding.UTF8.GetBytes(value));
    }

    private static string Hash(byte[] value) =>
        Convert.ToHexStringLower(SHA256.HashData(value))[..HashHexLength];
}
