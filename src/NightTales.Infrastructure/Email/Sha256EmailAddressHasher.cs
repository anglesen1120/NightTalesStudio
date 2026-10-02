using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Email;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The SHA-256 address hash of <c>email_messages.to_hash</c> (09 README BD9/BD16, 09.2.6-T3): the
/// normalized recipient address with the deployment's pepper appended, hex-encoded.
/// </summary>
/// <remarks>
/// The chain is the one BD9 fixes — <c>SHA-256(lowercase(trim(address)) + pepper)</c> — and it is the only
/// form of an address this feature ever stores or logs. The pepper is what keeps the value from being a
/// dictionary lookup away from the address it stands for.
/// </remarks>
/// <param name="options">The <c>Email</c> section, whose pepper is validated as non-empty at start-up.</param>
internal sealed class Sha256EmailAddressHasher(IOptions<EmailOptions> options) : IEmailAddressHasher
{
    /// <inheritdoc />
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "09 README BD9 fixes `SHA-256(lowercase(trim(address)) + pepper)`; upper-casing would change the stored hash of every existing row.")]
    public string Hash(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var normalized = address.Trim().ToLowerInvariant();
        var peppered = string.Concat(normalized, options.Value.AddressHashPepper);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(peppered));

        return Convert.ToHexStringLower(hash);
    }
}
