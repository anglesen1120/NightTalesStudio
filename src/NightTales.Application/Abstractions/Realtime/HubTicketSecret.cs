using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace NightTales.Application.Abstractions.Realtime;

/// <summary>
/// The ticket value of 09 README BD11: 32 random bytes as unpadded base64url on the wire, and the
/// lowercase SHA-256 hex of that text as the only form a row stores (BD16).
/// </summary>
/// <remarks>
/// <para>
/// The value is generated here rather than in the store because both halves must agree on it: the
/// issue handler hands the plaintext to the client and the hash to the table, and the claim of
/// <see cref="IHubTicketValidator"/> hashes what the client presented before it looks the row up. One
/// implementation therefore keeps the issue path and the claim path from drifting apart.
/// </para>
/// <para>
/// Hashing uses UTF-8 on purpose, not ASCII: the ticket is base64url (pure ASCII), but a client can
/// present anything, and <see cref="Encoding.ASCII"/> would fold every non-ASCII byte to <c>?</c> —
/// two different presented values would then share one hash. UTF-8 is the repo's hashing convention
/// (<c>IdempotencyBehavior</c>, <c>Es256TokenIssuer</c>) and the ASCII input makes both encodings
/// identical for every ticket this type issues.
/// </para>
/// </remarks>
public static class HubTicketSecret
{
    /// <summary>The entropy of one ticket: 32 random bytes, 256 bits (BD16).</summary>
    public const int Bytes = 32;

    /// <summary>The length of the base64url form of <see cref="Bytes"/>: 43 characters, no padding.</summary>
    public const int Length = 43;

    /// <summary>The length of the stored <c>SHA-256</c> form: 64 lowercase hex characters.</summary>
    public const int HashLength = 64;

    /// <summary>
    /// The UTF-8 size the hash buffer keeps on the stack: <see cref="Length"/> base64url characters
    /// plus room for a padded or otherwise malformed presentation, without putting an untrusted length
    /// on the stack.
    /// </summary>
    private const int MaxStackUtf8Bytes = 256;

    /// <summary>Creates one ticket: <see cref="Bytes"/> random bytes in unpadded base64url.</summary>
    public static string NewTicket()
    {
        // The entropy never needs to leave the stack: the ticket value is base64url-encoded straight
        // from it (BD16), so nothing but the returned string is allocated.
        Span<byte> entropy = stackalloc byte[Bytes];
        RandomNumberGenerator.Fill(entropy);

        return Base64Url.EncodeToString(entropy);
    }

    /// <summary>
    /// The stored form of <paramref name="ticket"/>: lowercase SHA-256 hex over its UTF-8 bytes
    /// (BD16). The plaintext is never persisted, logged or returned by this method.
    /// </summary>
    /// <param name="ticket">The value the client was handed, or presented.</param>
    public static string HashOf(string ticket)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticket);

        // A ticket this type issues is 43 ASCII characters, but a client may present anything, so the
        // UTF-8 buffer is a fixed stack slab up to the largest value a real ticket can be, and a
        // pooled buffer beyond it — never a stackalloc sized by the untrusted length.
        var byteCount = Encoding.UTF8.GetByteCount(ticket);
        byte[]? rented = null;
        try
        {
            Span<byte> utf8 = byteCount <= MaxStackUtf8Bytes
                ? stackalloc byte[MaxStackUtf8Bytes]
                : rented = ArrayPool<byte>.Shared.Rent(byteCount);

            var written = Encoding.UTF8.GetBytes(ticket, utf8);

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8[..written], digest);

            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
