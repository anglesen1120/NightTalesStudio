using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The one ES256 signing key of the process (09.3.2-T1 Design, W2-O3): the P-256 key loaded from the PEM
/// secret at start-up, its key id, and the JWKs that sign and validate with it.
/// </summary>
/// <remarks>
/// <para>
/// The key id is <c>SHA256(SubjectPublicKeyInfo)[..16]</c> in lowercase hex (BD3), so it changes exactly
/// when the key changes: a deploy-time rotation is visible in the tokens' <c>kid</c> header without any
/// extra bookkeeping.
/// </para>
/// <para>
/// No two signature providers share a crypto object. Signing builds a throwaway key from the PEM for every
/// signature and its factory caches no provider, so the provider <c>Microsoft.IdentityModel</c> releases —
/// and disposes — can only reach that one call's key: a key that wraps a single shared
/// <see cref="ECDsa"/> signs once and then throws <see cref="ObjectDisposedException"/> (observed as a 500
/// on the second login in one host, 2026-10-01). Validation pins the public half only, with a
/// crypto-provider factory of its own rather than the library's process-wide default, so its cache never
/// outlives this key.
/// </para>
/// <para>
/// Signing and validation therefore use two different keys: the private one signs, the public-only one is
/// what the bearer scheme pins. A validator can never reach the private parameters.
/// </para>
/// <para>
/// Construction is the start-up gate: a missing, malformed, non-EC, non-P-256 or public-only key throws,
/// and nothing downstream can fall back to another algorithm or a weaker curve (W2-O3). No failure message
/// echoes any part of the PEM (09 README D17).
/// </para>
/// </remarks>
public sealed class JwtSigningKey : IDisposable
{
    /// <summary>The number of hex characters of <c>SHA256(SubjectPublicKeyInfo)</c> used as the key id.</summary>
    public const int KeyIdLength = 16;

    /// <summary>The only curve ES256 accepts: P-256.</summary>
    /// <summary>
    /// The one message the start-up validation can print: it names the secret to set and every rule a key
    /// must satisfy, and it never contains key material (09 README D17).
    /// </summary>
    public const string ConfigurationFailure =
        "The ES256 signing key (secret nts/<env>/api/JWT_SIGNING_KEY) must be the PEM of a P-256 EC private key; "
        + "a missing, malformed, non-EC or public-only key stops the host (W2-O3).";

    private const string MissingKeyFailure =
        "The ES256 signing key is missing: set the secret nts/<env>/api/JWT_SIGNING_KEY to the PEM of a P-256 private key.";

    private const string MalformedKeyFailure =
        "The ES256 signing key is malformed: it must be the PEM of an EC private key (PKCS#8 or SEC1).";

    private const string WrongKeyFailure =
        "The ES256 signing key is not a P-256 private key: ES256 requires a P-256 key with a private part (W2-O3).";

    /// <summary>
    /// The factory a signature's throwaway key uses, with provider caching off. <c>Microsoft.IdentityModel</c> releases — and
    /// therefore disposes — the <see cref="SecurityKey"/> a signature provider was built from, while the
    /// <em>cached</em> provider survives; the next call would receive a cached provider holding a disposed
    /// key, which is exactly the <see cref="ObjectDisposedException"/> this type exists to avoid. Nothing is
    /// cached, so every use builds a provider over a key of its own and disposes only that one.
    /// </summary>
    private static readonly CryptoProviderFactory NonCachingFactory = new() { CacheSignatureProviders = false };

    private readonly string _pem;

    private readonly ECDsa _validationKey;

    private JwtSigningKey(string pem, ECDsa validationKey, string keyId)
    {
        _pem = pem;
        _validationKey = validationKey;
        KeyId = keyId;

        // A crypto-provider factory of this key's own instead of the library's process-wide
        // CryptoProviderFactory.Default. That default carries one provider cache for the whole process and
        // its cache key is the key's JWK thumbprint — identical for every key loaded from the same PEM. A
        // verifier cached for one host's key keeps that host's ECDsa, so once the host disposed the key
        // (see Dispose) the next host's first bearer request was handed the cached verifier and answered
        // 401 — IDX10511 over ObjectDisposedException — instead of 204 (2026-10-01). Caching stays on; it is
        // scoped to this key's lifetime instead of the process's.
        ValidationKey = new ECDsaSecurityKey(validationKey)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = true },
        };
    }

    /// <summary>The <c>kid</c> every token this key signs carries.</summary>
    public string KeyId { get; }

    /// <summary>The public-only key the bearer scheme pins; carries <see cref="KeyId"/>.</summary>
    public SecurityKey ValidationKey { get; }

    /// <summary>
    /// The credentials for <em>one</em> signature: a key of its own, parsed from the PEM.
    /// </summary>
    /// <remarks>
    /// A fresh key per signature is what keeps <c>Microsoft.IdentityModel</c>'s ownership rules harmless:
    /// the library releases — and disposes — the key a signature provider was built from, so a shared key
    /// signs once and throws <see cref="ObjectDisposedException"/> afterwards (observed as a 500 on a host's
    /// second login, 2026-10-01). The throwaway key's provider is never cached, so the disposal reaches only
    /// the key of that one call.
    /// </remarks>
    public SigningCredentials CreateSigningCredentials()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_pem);

        var key = new ECDsaSecurityKey(ecdsa) { KeyId = KeyId, CryptoProviderFactory = NonCachingFactory };

        return new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
    }

    /// <summary>Loads the key, throwing with a constant, key-free message when it is unusable.</summary>
    /// <param name="pem">The PEM of a P-256 EC private key, from the secret store.</param>
    /// <exception cref="InvalidOperationException">The key is missing, malformed or not a P-256 private key.</exception>
    public static JwtSigningKey FromPem(string? pem)
    {
        if (TryCreate(pem, out var key, out var failure))
        {
            return key!;
        }

        throw new InvalidOperationException(failure);
    }

    /// <summary>
    /// The non-throwing form of <see cref="FromPem"/>, used by the options validator so a bad key is a
    /// start-up failure with a readable message instead of an exception from a DI factory.
    /// </summary>
    /// <param name="pem">The PEM to load, or null/blank.</param>
    /// <param name="key">The loaded key, or null when the PEM is unusable.</param>
    /// <param name="failure">A constant message that never contains key material.</param>
    public static bool TryCreate(string? pem, out JwtSigningKey? key, out string failure)
    {
        key = null;

        if (string.IsNullOrWhiteSpace(pem))
        {
            failure = MissingKeyFailure;
            return false;
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            failure = MalformedKeyFailure;
            return false;
        }

        // The CURVE decides whether ES256 can sign, and the named curve's OID is what identifies it: a
        // 256-bit key on another curve (secp256k1 is the classic one) has the same key size and would sign
        // something no ES256 validator accepts. Only a key that really holds a private part can sign at
        // all: a PEM with just the public key imports cleanly and would fail much later, per request.
        if (!IsP256(ecdsa) || !HasPrivateKey(ecdsa))
        {
            failure = WrongKeyFailure;
            return false;
        }

        var keyId = ComputeKeyId(ecdsa);

        // The validator gets the public half only, and keeps it for the process's lifetime: its provider
        // caching stays on (the default), which is what stops the library from releasing — and disposing —
        // the key it validates with.
        var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(ecdsa.ExportSubjectPublicKeyInfo(), out _);

        key = new JwtSigningKey(pem, publicKey, keyId);
        failure = string.Empty;
        return true;
    }

    /// <summary>The key id of <paramref name="key"/>: <c>SHA256(SubjectPublicKeyInfo)[..16]</c>, lowercase hex.</summary>
    /// <param name="key">The EC key to identify.</param>
    public static string ComputeKeyId(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Convert.ToHexStringLower(
            SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..KeyIdLength];
    }

    /// <summary>Disposes the validation key with the host; the PEM needs no disposal.</summary>
    public void Dispose() => _validationKey.Dispose();

    /// <summary>Whether the key sits on the only curve ES256 accepts, by OID (2026-10-01 review, CR-3).</summary>
    private static bool IsP256(ECDsa key)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;

        return string.Equals(
                curve.Oid.Value,
                ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal)
            || string.Equals(
                curve.Oid.FriendlyName,
                ECCurve.NamedCurves.nistP256.Oid.FriendlyName,
                StringComparison.Ordinal);
    }

    private static bool HasPrivateKey(ECDsa key)
    {
        try
        {
            // A public-only key throws here; the export is discarded immediately — it exists to answer the
            // question, and the private bytes never leave this scope.
            CryptographicOperations.ZeroMemory(key.ExportECPrivateKey());
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
