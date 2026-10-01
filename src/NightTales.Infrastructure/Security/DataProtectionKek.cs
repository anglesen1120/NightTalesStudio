using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The <c>DP_KEK</c> key-encryption key of the process (09 README WF15, 09.9.4-T1): the certificate
/// loaded from the <c>DataProtection:KekPfxPath</c> PFX that wraps the Data Protection key ring before
/// it is written to <c>data_protection_keys</c>.
/// </summary>
/// <remarks>
/// <para>
/// Loading is the start-up gate. A missing path, a missing file, a wrong password, a certificate without
/// a private key or one without an RSA key all stop the host with one fixed message that names the
/// configuration key and never the value — a host that cannot protect its ring must not serve traffic,
/// and a host that started with a wrong KEK would write keys nobody can read back.
/// </para>
/// <para>
/// RSA is required, not a preference: the framework's <c>ProtectKeysWithCertificate</c> encrypts the key
/// XML with <c>System.Security.Cryptography.Xml.EncryptedXml</c>, which is RSA-only
/// (<c>CertificateXmlEncryptor</c>/<c>EncryptedXmlDecryptor</c> call <c>GetRSAPrivateKey</c>). An EC key
/// would be accepted by the PFX loader and then fail at the first protection with a far less clear error.
/// </para>
/// <para>
/// The certificate is loaded with <see cref="X509KeyStorageFlags.EphemeralKeySet"/> on Linux and Windows,
/// so the private key stays in memory and never reaches a key store. macOS refuses that flag outright —
/// <c>X509CertificateLoader</c> throws <see cref="PlatformNotSupportedException"/> (verified: macOS 27,
/// .NET 10.0.12) — and its loader without the flag imports the key into a <em>temporary</em> keychain that
/// is disposed with the certificate, so nothing is persisted on macOS either.
/// </para>
/// </remarks>
public sealed class DataProtectionKek : IDisposable
{
    /// <summary>
    /// Where the imported private key lives: memory (Linux, Windows) or a temporary keychain (macOS).
    /// </summary>
    private static X509KeyStorageFlags KeyStorageFlags => OperatingSystem.IsMacOS()
        ? X509KeyStorageFlags.DefaultKeySet
        : X509KeyStorageFlags.EphemeralKeySet;

    /// <summary>The failure reported when <c>DataProtection:KekPfxPath</c> is not configured.</summary>
    public const string MissingPathFailure =
        "'DataProtection:KekPfxPath' is required: it names the PFX of the DP_KEK key-encryption key "
        + "(secret nts/<env>/shared/DP_KEK, 11 §10:284).";

    /// <summary>The failure reported when the configured path names no file.</summary>
    public const string MissingFileFailure =
        "'DataProtection:KekPfxPath' does not name an existing PFX file.";

    /// <summary>The failure reported when the PFX cannot be read with the configured password.</summary>
    public const string UnreadableFailure =
        "'DataProtection:KekPfxPath' could not be read as a PKCS#12 certificate with the configured "
        + "'DataProtection:KekPfxPassword' (WF15).";

    /// <summary>The failure reported when the certificate carries no private key.</summary>
    public const string MissingPrivateKeyFailure =
        "'DataProtection:KekPfxPath' has no private key, so the key ring could not be decrypted with it (WF15).";

    /// <summary>The failure reported when the certificate carries no RSA key.</summary>
    public const string MissingRsaKeyFailure =
        "'DataProtection:KekPfxPath' has no RSA key; the key ring is encrypted with EncryptedXml, which "
        + "accepts RSA only (WF15).";

    private DataProtectionKek(X509Certificate2 certificate) => Certificate = certificate;

    /// <summary>The certificate whose public key encrypts the ring and whose private key reads it back.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Loads the KEK, or throws the fixed start-up failure.</summary>
    /// <param name="options">The bound <c>DataProtection</c> section.</param>
    /// <exception cref="OptionsValidationException">
    /// The configuration does not name a usable KEK PFX. The message names the configuration key and
    /// never the path's password nor any key material.
    /// </exception>
    public static DataProtectionKek Load(DataProtectionKeyRingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (TryLoad(options, out var kek, out var failure))
        {
            return kek;
        }

        throw new OptionsValidationException(
            DataProtectionKeyRingOptions.SectionName,
            typeof(DataProtectionKeyRingOptions),
            [failure]);
    }

    /// <summary>Loads the KEK without throwing.</summary>
    /// <param name="options">The bound <c>DataProtection</c> section.</param>
    /// <param name="kek">The loaded KEK when the answer is <see langword="true"/>.</param>
    /// <param name="failure">The fixed failure message when the answer is <see langword="false"/>.</param>
    public static bool TryLoad(
        DataProtectionKeyRingOptions options,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DataProtectionKek? kek,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? failure)
    {
        ArgumentNullException.ThrowIfNull(options);

        kek = null;

        if (options.KekPfxPath is not { } path)
        {
            failure = MissingPathFailure;

            return false;
        }

        if (!File.Exists(path))
        {
            failure = MissingFileFailure;

            return false;
        }

        X509Certificate2 certificate;

        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                options.KekPfxPassword,
                KeyStorageFlags);
        }
        catch (CryptographicException)
        {
            // A wrong password, a truncated file or a plain PEM: all of them are the same configuration
            // failure. The exception is dropped rather than wrapped — its message may echo the path and,
            // for some providers, key material.
            failure = UnreadableFailure;

            return false;
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            failure = MissingPrivateKeyFailure;

            return false;
        }

        using (var rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is null)
            {
                certificate.Dispose();
                failure = MissingRsaKeyFailure;

                return false;
            }
        }

        failure = null;
        kek = new DataProtectionKek(certificate);

        return true;
    }

    /// <inheritdoc />
    public void Dispose() => Certificate.Dispose();
}
