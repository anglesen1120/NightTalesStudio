using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NightTales.TestKit.Security;

/// <summary>
/// The throwaway <c>DP_KEK</c> of one test run (09.9.4-T1): a self-signed RSA certificate exported as a
/// PFX into the process's temporary directory, generated on first use and deleted when the process exits.
/// </summary>
/// <remarks>
/// <para>
/// The Api and the Worker validate <c>DataProtection:KekPfxPath</c> on start-up, so every test host needs
/// a real PFX on disk. This is that PFX, and it is never a production certificate: the key is generated
/// in-process for this run, never committed, never installed in a key store
/// (<see cref="X509KeyStorageFlags.EphemeralKeySet"/> is what the host loads it with), and its password is
/// random per file.
/// </para>
/// <para>
/// RSA and not ECDSA, because the ring is encrypted with <c>EncryptedXml</c>, which accepts an RSA key
/// only. The key usage is key encipherment for the same reason.
/// </para>
/// <para>
/// <see cref="SharedPath"/> is the instance that makes two test hosts of one run share a key ring — that
/// is what proves "a second instance with the same <c>DP_KEK</c> unprotects the first instance's value".
/// <see cref="Create"/> makes an independent one, which proves the opposite.
/// </para>
/// </remarks>
public static class TestKek
{
    /// <summary>The configuration key of the PFX path.</summary>
    public const string PathConfigurationKey = "DataProtection:KekPfxPath";

    /// <summary>The configuration key of the PFX password.</summary>
    public const string PasswordConfigurationKey = "DataProtection:KekPfxPassword";

    private static readonly Lazy<TestKekFile> Shared = new(() => Create("nts-test-shared-dp-kek"));

    private static readonly List<string> FilesToDelete = [];

    /// <summary>The PFX every host of this run shares.</summary>
    public static string SharedPath => Shared.Value.Path;

    /// <summary>The password of <see cref="SharedPath"/>.</summary>
    public static string SharedPassword => Shared.Value.Password;

    /// <summary>Creates one more independent KEK, for the "wrong certificate" case.</summary>
    /// <param name="commonName">The certificate's subject, only used to name the file.</param>
    public static TestKekFile Create(string commonName = "nts-test-dp-kek")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, critical: false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var path = Path.Combine(Path.GetTempPath(), $"{commonName}-{Guid.NewGuid():N}.pfx");

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        Register(path);

        return new TestKekFile(path, password);
    }

    private static void Register(string path)
    {
        lock (FilesToDelete)
        {
            if (FilesToDelete.Count == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteAll();
            }

            FilesToDelete.Add(path);
        }
    }

    private static void DeleteAll()
    {
        lock (FilesToDelete)
        {
            foreach (var path in FilesToDelete)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A KEK that outlives the run in /tmp is harmless (it is a throwaway self-signed
                    // certificate); failing the run over it is not worth it.
                }
            }

            FilesToDelete.Clear();
        }
    }
}

/// <summary>One generated test KEK: the PFX file and the password it was exported with.</summary>
/// <param name="path">The absolute PFX path.</param>
/// <param name="password">The PFX password.</param>
public sealed record TestKekFile(string Path, string Password);
