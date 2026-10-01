using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NightTales.Infrastructure.Security;
using NightTales.TestKit.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The <c>DP_KEK</c> start-up gate (09.9.4-T1): an absent, unreadable, private-key-less or non-RSA PFX
/// must stop the host with one fixed message that names the configuration key and never the value.
/// </summary>
/// <remarks>
/// These are the cases the task's failure table names. The PFX fixtures are generated in-process
/// (<see cref="TestKek"/> and the two local helpers below); no certificate is committed and none is a
/// production one.
/// </remarks>
public sealed class DataProtectionKekTests
{
    [Fact]
    public void Missing_path_is_rejected_with_the_fixed_message()
    {
        var options = new DataProtectionKeyRingOptions { KekPfxPath = null, KekPfxPassword = "irrelevant" };

        DataProtectionKek.TryLoad(options, out var kek, out var failure).ShouldBeFalse();

        kek.ShouldBeNull();
        failure.ShouldBe(DataProtectionKek.MissingPathFailure);
    }

    [Fact]
    public void Missing_file_is_rejected_with_the_fixed_message()
    {
        var options = new DataProtectionKeyRingOptions
        {
            KekPfxPath = Path.Combine(Path.GetTempPath(), $"nts-absent-{Guid.NewGuid():N}.pfx"),
            KekPfxPassword = "irrelevant",
        };

        DataProtectionKek.TryLoad(options, out _, out var failure).ShouldBeFalse();

        failure.ShouldBe(DataProtectionKek.MissingFileFailure);
    }

    [Fact]
    public void Wrong_password_is_rejected_with_the_fixed_message()
    {
        const string WrongPassword = "not-the-password-of-this-pfx";
        var key = TestKek.Create("nts-test-wrong-password");

        var options = new DataProtectionKeyRingOptions { KekPfxPath = key.Path, KekPfxPassword = WrongPassword };

        DataProtectionKek.TryLoad(options, out _, out var failure).ShouldBeFalse();

        failure.ShouldBe(DataProtectionKek.UnreadableFailure);
        failure.ShouldNotContain(WrongPassword);
        failure.ShouldNotContain(key.Path);
    }

    [Fact]
    public void Pfx_without_a_private_key_is_rejected_with_the_fixed_message()
    {
        var path = WritePfxWithoutPrivateKey();

        var options = new DataProtectionKeyRingOptions { KekPfxPath = path, KekPfxPassword = null };

        DataProtectionKek.TryLoad(options, out _, out var failure).ShouldBeFalse();

        failure.ShouldBe(DataProtectionKek.MissingPrivateKeyFailure);
    }

    [Fact]
    public void Non_rsa_certificate_is_rejected_with_the_fixed_message()
    {
        var path = WriteEcPfx();

        var options = new DataProtectionKeyRingOptions { KekPfxPath = path, KekPfxPassword = "ec" };

        DataProtectionKek.TryLoad(options, out _, out var failure).ShouldBeFalse();

        // The ring is encrypted with EncryptedXml, which accepts RSA only: an EC key would fail at the
        // first protection with a far less clear error, so it is refused here.
        failure.ShouldBe(DataProtectionKek.MissingRsaKeyFailure);
    }

    [Fact]
    public void Valid_kek_loads_with_its_private_key()
    {
        var key = TestKek.Create("nts-test-valid-kek");

        var options = new DataProtectionKeyRingOptions { KekPfxPath = key.Path, KekPfxPassword = key.Password };

        DataProtectionKek.TryLoad(options, out var kek, out var failure).ShouldBeTrue();

        failure.ShouldBeNull();
        kek.ShouldNotBeNull();
        using (kek)
        {
            kek.Certificate.HasPrivateKey.ShouldBeTrue();
            using var rsa = kek.Certificate.GetRSAPrivateKey();
            rsa.ShouldNotBeNull();
        }
    }

    [Fact]
    public void Load_throws_the_fixed_failure_when_the_pfx_is_unusable()
    {
        var options = new DataProtectionKeyRingOptions { KekPfxPath = null };

        var exception = Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(
            () => DataProtectionKek.Load(options));

        exception.Failures.ShouldContain(DataProtectionKek.MissingPathFailure);
    }

    /// <summary>A real PKCS#12 that carries the certificate and no private key.</summary>
    private static string WritePfxWithoutPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        using var withKey = CreateSelfSigned(new CertificateRequest(
            "CN=nts-test-public-only",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

        return WritePfx(publicOnly.Export(X509ContentType.Pkcs12), "nts-test-public-only");
    }

    /// <summary>A real PKCS#12 whose private key is EC, which the ring's EncryptedXml cannot use.</summary>
    private static string WriteEcPfx()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CreateSelfSigned(new CertificateRequest(
            "CN=nts-test-ec-kek",
            ecdsa,
            HashAlgorithmName.SHA256));

        return WritePfx(certificate.Export(X509ContentType.Pkcs12, "ec"), "nts-test-ec-kek");
    }

    private static X509Certificate2 CreateSelfSigned(CertificateRequest request) =>
        request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

    private static string WritePfx(byte[] bytes, string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, bytes);

        return path;
    }
}
