using System.Security.Cryptography;

namespace NightTales.TestKit.Api;

/// <summary>
/// The ephemeral ES256 key the test hosts sign and validate with (09.3.2-T1).
/// </summary>
/// <remarks>
/// <para>
/// The production key is a secret (<c>nts/&lt;env&gt;/api/JWT_SIGNING_KEY</c>) and is never committed; a
/// test host therefore has to supply one, exactly as a deployment does. The PEM here is generated once per
/// test run, in memory, and never written anywhere — the token format is the real one, the key simply
/// belongs to nobody.
/// </para>
/// <para>
/// A P-256 key, because ES256 requires it: a test that signed with a weaker curve would prove nothing
/// about production.
/// </para>
/// </remarks>
public static class TestSigningKey
{
    /// <summary>The configuration key the hosts are given, <c>Auth:Jwt:SigningKey</c>.</summary>
    public const string ConfigurationKey = "Auth:Jwt:SigningKey";

    /// <summary>The PEM of this test run's ephemeral P-256 private key.</summary>
    public static string Pem { get; } = CreatePem();

    private static string CreatePem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return key.ExportECPrivateKeyPem();
    }
}
