using System.Diagnostics.CodeAnalysis;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// Protects and reads back one platform value under one purpose (09 README BD12/WF15): the seam
/// <c>06</c> uses for a channel-connection token and <c>09.10.1</c> for a stored provider secret.
/// </summary>
/// <remarks>
/// <para>
/// The purpose is the security property, not a label: the implementation protects under the fixed root
/// <c>tokens.v1/</c> followed by <paramref name="purposeSuffix"/>, so a cipher written for one
/// connection can never be unprotected under another one's purpose (BD12's
/// <c>tokens.v1/{connectionId}</c>). Every caller passes a suffix of its own — the connection id for
/// <c>06</c>, <c>settings.v1/{integration}/{key}</c> for <c>09.10.1</c>.
/// </para>
/// <para>
/// The TOTP secret store does <em>not</em> use this port: it calls
/// <c>IDataProtectionProvider.CreateProtector("identity.totp.v1")</c> directly on the same key ring,
/// because its purpose <em>root</em> is <c>identity.totp.v1</c> and not <c>tokens.v1/*</c> — going
/// through this interface would silently produce <c>tokens.v1/identity.totp.v1</c> and break that
/// isolation root [RULED — README §B10 WT43].
/// </para>
/// <para>
/// A caller treats <see cref="TryUnprotect"/> returning <see langword="false"/> as "invalid token" and
/// answers one uniform error: the failure is never a stack trace and never an oracle that says whether
/// the cipher or the purpose was wrong. When a caller compares a protected value against a stored one it
/// uses <c>CryptographicOperations.FixedTimeEquals</c> (BD16).
/// </para>
/// </remarks>
public interface ITokenProtector
{
    /// <summary>Protects <paramref name="plaintext"/> for <paramref name="purposeSuffix"/>.</summary>
    /// <param name="purposeSuffix">The caller's purpose under the <c>tokens.v1/</c> root; never empty.</param>
    /// <param name="plaintext">The value to protect; never empty, never logged.</param>
    /// <returns>The cipher as base64, safe to store in a text column.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="purposeSuffix"/> is null, empty or whitespace, or <paramref name="plaintext"/> is
    /// null or empty — both are programming errors of the caller, not a request outcome.
    /// </exception>
    string Protect(string purposeSuffix, string plaintext);

    /// <summary>
    /// Reads back a cipher written by <see cref="Protect"/> under the same
    /// <paramref name="purposeSuffix"/>.
    /// </summary>
    /// <remarks>
    /// A tampered, truncated, foreign-purpose or foreign-key-ring cipher is a <see langword="false"/>
    /// answer, never an exception: the framework's authenticated encryption is the check. The reason is
    /// logged at <c>Warning</c> with the purpose only — the cipher and the plaintext never reach a log.
    /// </remarks>
    /// <param name="purposeSuffix">The purpose the cipher was written for; never empty.</param>
    /// <param name="cipher">The base64 cipher to read.</param>
    /// <param name="plaintext">The plaintext when the answer is <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="cipher"/> belongs to this purpose.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="purposeSuffix"/> is null, empty or whitespace.
    /// </exception>
    bool TryUnprotect(string purposeSuffix, string cipher, [NotNullWhen(true)] out string? plaintext);
}
