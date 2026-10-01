using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// <see cref="ITokenProtector"/> over the platform's ASP.NET Core Data Protection key ring (09 README
/// BD12/WF15): one <see cref="IDataProtector"/> per purpose, cached for the process.
/// </summary>
/// <remarks>
/// <para>
/// The purpose chain is fixed at <c>tokens.v1/{purposeSuffix}</c>. Data Protection derives one
/// subkey per purpose from the ring's master key, and its authenticated encryption makes a cipher
/// undecryptable under any other purpose — that is exactly the isolation BD12 asks for. The suffix is
/// never re-split here: the framework splits the purpose string on '/' itself, so
/// <c>tokens.v1/settings.v1/mpt/api_key</c> and the equivalent nested chain are the same purpose.
/// </para>
/// <para>
/// The ring itself is persistence-owned (<c>PersistKeysToDbContext&lt;AppDbContext&gt;</c>) and encrypted
/// with the <c>DP_KEK</c> certificate (WF15), so this type holds no key material and never touches the
/// database: the framework's key manager does, through its own scope.
/// </para>
/// <para>
/// Nothing here ever puts a plaintext or a cipher into a log, an exception message or a cached field.
/// The one log line this type writes is on a failed unprotect and names the purpose — an identifier the
/// caller chose, not a value (BD16).
/// </para>
/// </remarks>
/// <param name="provider">The host's key ring (the Api and the Worker share one).</param>
/// <param name="logger">The host's logger; only the failure line of <see cref="TryUnprotect"/> uses it.</param>
public sealed partial class DataProtectionTokenProtector(
    IDataProtectionProvider provider,
    ILogger<DataProtectionTokenProtector> logger) : ITokenProtector
{
    /// <summary>The fixed purpose root of every value this port protects (BD12).</summary>
    public const string PurposeRoot = "tokens.v1";

    private readonly ConcurrentDictionary<string, IDataProtector> _protectors = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Protect(string purposeSuffix, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purposeSuffix);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var protector = ProtectorFor(purposeSuffix);
        var cipher = protector.Protect(Encoding.UTF8.GetBytes(plaintext));

        return Convert.ToBase64String(cipher);
    }

    /// <inheritdoc />
    public bool TryUnprotect(string purposeSuffix, string cipher, [NotNullWhen(true)] out string? plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purposeSuffix);

        plaintext = null;

        // A malformed cipher is a caller's bad input, not a reason to throw: the contract is one boolean
        // answer, and `Convert.FromBase64String` would otherwise turn it into a 500.
        if (string.IsNullOrEmpty(cipher) || !TryDecode(cipher, out var bytes))
        {
            LogUnprotectFailure(purposeSuffix);

            return false;
        }

        try
        {
            plaintext = Encoding.UTF8.GetString(ProtectorFor(purposeSuffix).Unprotect(bytes));

            return true;
        }
        catch (CryptographicException)
        {
            // Wrong purpose, tampered payload, or a key from another ring: all of them are the same
            // answer to the caller, and none of them may leak the value into the log.
            LogUnprotectFailure(purposeSuffix);

            return false;
        }
    }

    private static bool TryDecode(string cipher, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(cipher);

            return true;
        }
        catch (FormatException)
        {
            bytes = [];

            return false;
        }
    }

    private IDataProtector ProtectorFor(string purposeSuffix)
        // The purpose is cached per suffix: CreateProtector is not free and the ring's subkey derivation
        // is what the framework caches, not the protector instance.
        => _protectors.GetOrAdd(purposeSuffix, suffix => provider.CreateProtector($"{PurposeRoot}/{suffix}"));

    // The purpose is an identifier the caller chose, never a value: no plaintext and no cipher reaches a
    // log line, an exception message or a metric tag (BD16).
    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Warning,
        Message = "token.protector.unprotect.failed purpose={Purpose}: the caller answers one uniform invalid-token error.")]
    private partial void LogUnprotectFailure(string purpose);
}
