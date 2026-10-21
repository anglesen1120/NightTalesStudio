using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// Makes the self-signed server certificates the pinned-TLS tests need (11.3.3-T1 plan §9 ⚠️, D20 H12):
/// an RSA certificate with the server-authentication usage a TLS listener expects, valid for exactly the
/// window the test asks for, so the factory's pin and expiry rules are exercised by a real handshake.
/// </summary>
public static class TestCertificates
{
    /// <summary>
    /// A self-signed server certificate for <c>127.0.0.1</c>/<c>localhost</c>, carrying its private key.
    /// </summary>
    /// <param name="notBefore">The start of the validity window (UTC).</param>
    /// <param name="notAfter">The end of the validity window (UTC); an instant in the past makes an expired certificate.</param>
    /// <param name="commonName">The subject's common name.</param>
    public static X509Certificate2 Server(
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string commonName = "nts-test-node")
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        using var created = request.CreateSelfSigned(notBefore, notAfter);

        // Export/import as PKCS#12: the platform TLS stack needs the private key in a form it can use as a
        // server identity on every OS (macOS refuses some ephemeral-only keys).
        return X509CertificateLoader.LoadPkcs12(
            created.Export(X509ContentType.Pkcs12),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>The pinned fingerprint of a certificate, in the form the column and the config use.</summary>
    /// <param name="certificate">The certificate the node presents.</param>
    public static string PinSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }
}
