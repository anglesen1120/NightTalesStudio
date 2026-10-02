using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace NightTales.MptAdapter.Tls;

/// <summary>The loaded node certificate plus the facts the operator pins and rotates by (D11, T57).</summary>
public sealed record LoadedCertificate(X509Certificate2 Certificate, string Sha256Fingerprint, DateTimeOffset NotAfter);

/// <summary>
/// Loads <c>/etc/nts/tls/adapter.crt|key</c> and refuses a missing, unparsable, not-yet-valid or expired
/// certificate with a fixed message (exit 2 at startup). The fingerprint and <c>NotAfter</c> are logged once so
/// the operator can pin <c>RenderNodes:Nodes[i]:TlsCertSha256</c> and rotate before expiry (README §10 T57).
/// </summary>
public static class TlsValidator
{
    public const string MissingMessage = "The adapter TLS certificate or key file is missing.";
    public const string UnparsableMessage = "The adapter TLS certificate or key could not be parsed.";
    public const string NotYetValidMessage = "The adapter TLS certificate is not yet valid.";
    public const string ExpiredMessage = "The adapter TLS certificate has expired.";

    /// <summary>Null failure means <paramref name="loaded"/> is usable.</summary>
    public static string? TryLoad(
        string certPath,
        string keyPath,
        TimeProvider clock,
        out LoadedCertificate? loaded)
    {
        loaded = null;
        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            return MissingMessage;
        }

        X509Certificate2 certificate;
        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);

            // Round-trip through PKCS#12 so Kestrel gets a certificate whose key is usable on every platform
            // (an ephemeral PEM key is not accepted by Schannel/SecureTransport).
            certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException)
        {
            return UnparsableMessage;
        }

        var now = clock.GetUtcNow();
        if (now < new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero))
        {
            certificate.Dispose();
            return NotYetValidMessage;
        }

        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (now > notAfter)
        {
            certificate.Dispose();
            return ExpiredMessage;
        }

        loaded = new LoadedCertificate(certificate, certificate.GetCertHashString(HashAlgorithmName.SHA256), notAfter);
        return null;
    }

    /// <summary>Logs the fingerprint and <c>NotAfter</c> exactly once per call; the host calls it once.</summary>
    public static void LogFingerprint(ILogger logger, LoadedCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(certificate);

        logger.TlsCertificate(certificate.Sha256Fingerprint, certificate.NotAfter);
    }
}
