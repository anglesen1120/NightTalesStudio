using System.Security.Cryptography;
using System.Text;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The SHA-1 hex forms the breached-password check uses (09.3.1-T1).
/// </summary>
/// <remarks>
/// SHA-1 is used <em>only</em> as the provider's k-anonymity key and as the lookup key of the bundled
/// top-list, never as a password store: the platform stores passwords through
/// <c>IPasswordHasher&lt;AppUser&gt;</c>. The provider's protocol fixes the digest, so SHA-1 cannot be
/// replaced by a stronger one here.
/// </remarks>
internal static class PasswordRangeKey
{
    /// <summary>The length of the k-anonymity prefix the range provider receives (5 hex characters).</summary>
    internal const int PrefixLength = 5;

    /// <summary>The uppercase hex SHA-1 of <paramref name="password"/> (40 characters).</summary>
    internal static string Full(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var utf8 = Encoding.UTF8.GetBytes(password);
        try
        {
#pragma warning disable CA5350 // SHA-1 is the range key the provider's protocol requires, not a password store.
            var digest = SHA1.HashData(utf8);
#pragma warning restore CA5350
            return Convert.ToHexString(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    /// <summary>The first <see cref="PrefixLength"/> hex characters of <see cref="Full"/>.</summary>
    internal static string Prefix(string password) => Full(password)[..PrefixLength];
}
