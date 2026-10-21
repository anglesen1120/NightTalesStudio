using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The ES256 issuer of 09.3.2-T1 (Tests table): the algorithm, the claims, the key id, the per-role
/// lifetimes, and the start-up refusal of a key that cannot sign.
/// </summary>
/// <remarks>
/// No database and no host: the issuer is a pure function of its key, its options and the clock, and the
/// tests decode the tokens with the same library the Api validates them with.
/// </remarks>
public sealed class Es256TokenIssuerTests
{
    private const string Issuer = "nts-Test";
    private const string Audience = "nts-api";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AccessToken_IsEs256_WithExpectedClaimsKidAndLifetimes()
    {
        var pem = IssueKey();
        var key = JwtSigningKey.FromPem(pem);
        var issuer = new Es256TokenIssuer(key, Options.Create(JwtOptionsForTest()));
        var user = User(PlatformRole.User);
        var familyId = Guid.CreateVersion7();

        // The issued-at instant is the real clock here: the token is validated with `ValidateLifetime`, and a
        // token minted in the past would be rejected as expired rather than tested.
        var now = DateTimeOffset.UtcNow;

        var token = issuer.IssueAccessToken(user, familyId, AuthMethod.Pwd, now);
        var jwt = new JsonWebToken(token.Token);

        jwt.Alg.ShouldBe("ES256", "W2-O3 fixes ES256 as the only algorithm");
        jwt.Kid.ShouldBe(token.Kid);
        jwt.Kid.ShouldBe(ExpectedKid(pem), "the kid is SHA256(SubjectPublicKeyInfo)[..16] of the signing key (BD3)");
        jwt.Issuer.ShouldBe(Issuer);
        jwt.Audiences.ShouldBe(new[] { Audience });
        jwt.Subject.ShouldBe(user.Id.ToString());
        jwt.GetClaim("role").Value.ShouldBe("user");
        jwt.GetClaim("sid").Value.ShouldBe(familyId.ToString());
        jwt.GetClaim("amr").Value.ShouldBe("pwd");
        jwt.GetClaim("stamp").Value.ShouldBe(user.SecurityStamp);
        jwt.GetClaim("jti").Value.ShouldNotBeNullOrWhiteSpace("every token carries its own 16 random bytes");
        jwt.GetClaim("jti").Value.Length.ShouldBe(32);

        token.ExpiresAt.ShouldBe(now.AddMinutes(15), "a user token lives 15 minutes (BD3)");
        (jwt.ValidTo - jwt.IssuedAt).ShouldBe(TimeSpan.FromMinutes(15));

        // The token really validates against the public half of the key, with the audience and issuer the Api
        // pins: a self-consistent header would not prove the signature.
        var validation = await new JsonWebTokenHandler()
            .ValidateTokenAsync(token.Token, new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key.ValidationKey,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
            });

        validation.IsValid.ShouldBeTrue(validation.Exception?.Message);
    }

    [Fact]
    public async Task TheSameIssuerSignsEveryTokenInItsLifetime()
    {
        // Regression for the shared-crypto-object trap: `Microsoft.IdentityModel` disposes the SecurityKey a
        // signature provider was built from, so a key that wraps one long-lived ECDsa signs once and then
        // throws ObjectDisposedException (observed as a 500 on a host's second login, 2026-10-01).
        var key = JwtSigningKey.FromPem(IssueKey());
        var issuer = new Es256TokenIssuer(key, Options.Create(JwtOptionsForTest()));
        var now = DateTimeOffset.UtcNow;
        var handler = new JsonWebTokenHandler();

        for (var round = 0; round < 5; round++)
        {
            var token = issuer.IssueAccessToken(
                User(PlatformRole.User),
                Guid.CreateVersion7(),
                AuthMethod.Pwd,
                now);

            var validation = await handler.ValidateTokenAsync(token.Token, new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key.ValidationKey,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            });

            validation.IsValid.ShouldBeTrue($"round {round}: {validation.Exception?.Message}");
        }
    }

    [Fact]
    public void AccessToken_ForOwnerAndAdmin_IsFiveMinutes()
    {
        var key = JwtSigningKey.FromPem(IssueKey());
        var issuer = new Es256TokenIssuer(key, Options.Create(JwtOptionsForTest()));

        foreach (var role in new[] { PlatformRole.Admin, PlatformRole.Owner })
        {
            var token = issuer.IssueAccessToken(User(role), Guid.CreateVersion7(), AuthMethod.Pwd, Now);
            var jwt = new JsonWebToken(token.Token);

            jwt.GetClaim("role").Value.ShouldBe(role.ToString().ToLowerInvariant());
            (jwt.ValidTo - jwt.IssuedAt).ShouldBe(
                TimeSpan.FromMinutes(5),
                $"{role} tokens live 5 minutes (BD3, WF12)");
        }
    }

    [Fact]
    public void EnrolmentToken_CarriesTheEnrolmentScope_AndFifteenMinutes()
    {
        var key = JwtSigningKey.FromPem(IssueKey());
        var issuer = new Es256TokenIssuer(key, Options.Create(JwtOptionsForTest()));
        var user = User(PlatformRole.Owner);

        var token = issuer.IssueEnrolmentToken(user, Now);
        var jwt = new JsonWebToken(token.Token);

        jwt.GetClaim("scope").Value.ShouldBe("mfa_enrol", "the enrolment token is accepted by /me/mfa/* only (BD4)");
        jwt.GetClaim("amr").Value.ShouldBe("pwd");
        jwt.Claims.Any(claim => claim.Type == "sid").ShouldBeFalse("an enrolment token belongs to no family");
        (jwt.ValidTo - jwt.IssuedAt).ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void MissingSigningKey_StartupFails()
    {
        // Absent, blank and malformed are all start-up failures, never a fallback algorithm (W2-O3).
        Should.Throw<InvalidOperationException>(() => JwtSigningKey.FromPem(null)).Message.ShouldContain("missing");
        Should.Throw<InvalidOperationException>(() => JwtSigningKey.FromPem("   ")).Message.ShouldContain("missing");
        Should.Throw<InvalidOperationException>(() => JwtSigningKey.FromPem("not a pem at all"))
            .Message.ShouldContain("malformed");

        JwtSigningKey.TryCreate("not a pem at all", out var malformed, out _).ShouldBeFalse();
        malformed.ShouldBeNull();
    }

    [Fact]
    public void SigningKey_ThatIsNotAP256PrivateKey_StartupFails()
    {
        // A public-only PEM and a P-384 key both import cleanly and would only fail per request: ES256 needs
        // a P-256 key with a private part, so both are start-up failures.
        using var publicOnly = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = publicOnly.ExportSubjectPublicKeyInfoPem();

        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var wrongCurvePem = wrongCurve.ExportECPrivateKeyPem();

        JwtSigningKey.TryCreate(publicPem, out var fromPublic, out var publicFailure).ShouldBeFalse();
        fromPublic.ShouldBeNull();
        publicFailure.ShouldContain("P-256");

        JwtSigningKey.TryCreate(wrongCurvePem, out var fromWrongCurve, out var curveFailure).ShouldBeFalse();
        fromWrongCurve.ShouldBeNull();
        curveFailure.ShouldContain("P-256");
    }

    [Fact]
    public void SigningKey_OnAnotherCurveWithTheSameKeySize_StartupFails()
    {
        // secp256k1 is also a 256-bit key: key size alone cannot tell it apart from P-256, and a signature
        // made with it is one no ES256 validator accepts. The platform may not support the curve at all, in
        // which case there is nothing to prove here.
        ECDsa curve;
        try
        {
            curve = ECDsa.Create(ECCurve.CreateFromValue("1.3.132.0.10"));
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            return;
        }

        using (curve)
        {
            var pem = curve.ExportECPrivateKeyPem();

            JwtSigningKey.TryCreate(pem, out var key, out var failure).ShouldBeFalse();
            key.ShouldBeNull();
            failure.ShouldContain("P-256");
        }
    }

    [Fact]
    public void Kid_IsStable_ForTheSameKey()
    {
        var pem = IssueKey();

        var first = JwtSigningKey.FromPem(pem);
        var second = JwtSigningKey.FromPem(pem);

        first.KeyId.ShouldBe(second.KeyId, "the same key always names itself the same way");
        first.KeyId.Length.ShouldBe(JwtSigningKey.KeyIdLength);

        var other = JwtSigningKey.FromPem(IssueKey());
        other.KeyId.ShouldNotBe(
            first.KeyId,
            "a different key gets a different kid, which is what a rotation looks like");
    }

    [Fact]
    public void ValidationKey_IsPublicOnly()
    {
        using var key = JwtSigningKey.FromPem(IssueKey());
        var validationKey = key.ValidationKey.ShouldBeOfType<ECDsaSecurityKey>();

        validationKey.KeyId.ShouldBe(key.KeyId);
        Should.Throw<CryptographicException>(
            () => validationKey.ECDsa.ExportECPrivateKey(),
            "a validator receives the public half only");

        // Signing always builds its own key; the derivation is the point, not the instance.
        key.CreateSigningCredentials().Key.ShouldNotBeNull();
    }

    [Fact]
    public async Task DisposingOneKey_DoesNotBreakAnotherKeyLoadedFromTheSamePem()
    {
        // Regression for the process-wide crypto-provider cache trap (2026-10-01): a key that leaves its
        // provider cache to CryptoProviderFactory.Default shares one process-wide cache, and the cache key
        // is the key's JWK thumbprint — identical for every key loaded from the same PEM. A verifier cached
        // for one host's key therefore keeps that host's ECDsa, and once the host disposes the key
        // (JwtSigningKey.Dispose) the next host's first bearer request hits the cached verifier and answers
        // 401 with IDX10511 over ObjectDisposedException, instead of 204
        // (NightTales.Api.IntegrationTests.Auth.LogoutTests.Logout_RevokesFamily_AndClearsCookie).
        var pem = IssueKey();
        using var first = JwtSigningKey.FromPem(pem);
        var second = JwtSigningKey.FromPem(pem);
        var handler = new JsonWebTokenHandler();
        var token = new Es256TokenIssuer(first, Options.Create(JwtOptionsForTest()))
            .IssueAccessToken(User(PlatformRole.User), Guid.CreateVersion7(), AuthMethod.Pwd, DateTimeOffset.UtcNow)
            .Token;

        // The first host validates: this is what caches a verifier for the key's thumbprint.
        var before = await handler.ValidateTokenAsync(token, ValidationParametersFor(first));
        before.IsValid.ShouldBeTrue(before.Exception?.Message);

        // The first host shuts down.
        first.Dispose();

        // A second host loads the same PEM and validates the same token: its own key must be the one used.
        var after = await handler.ValidateTokenAsync(token, ValidationParametersFor(second));
        after.IsValid.ShouldBeTrue(after.Exception?.Message);
    }

    /// <summary>The bearer scheme's validation parameters, pinned to one key — the same shape the Api sets.</summary>
    private static TokenValidationParameters ValidationParametersFor(JwtSigningKey key) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = key.ValidationKey,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void RefreshToken_IsBase64Url_AndOnlyItsHashIsStable()
    {
        var key = JwtSigningKey.FromPem(IssueKey());
        var issuer = new Es256TokenIssuer(key, Options.Create(JwtOptionsForTest()));

        var token = issuer.CreateRefreshToken();

        // 32 random bytes in base64url: 43 characters, no padding and no character a cookie header would
        // have to escape (BD3).
        token.Length.ShouldBe(43);
        token.ShouldNotContain('+');
        token.ShouldNotContain('/');
        token.ShouldNotContain('=');

        issuer.HashToken(token).Length.ShouldBe(64, "SHA-256 hex");
        issuer.HashToken(token).ShouldBe(issuer.HashToken(token), "the stored form is a pure function of the token");
        issuer.HashToken(token).ShouldNotBe(issuer.HashToken(issuer.CreateRefreshToken()));
        issuer.HashToken(token).ShouldNotContain(
            token,
            Case.Sensitive,
            "the plaintext can never be recovered from the hash store");
    }

    /// <summary>The kid a fresh derivation from the same PEM produces — the property the issuer claims.</summary>
    private static string ExpectedKid(string pem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);

        return JwtSigningKey.ComputeKeyId(key);
    }

    private static JwtOptions JwtOptionsForTest() => new()
    {
        Jwt = new JwtOptions.JwtSection { Issuer = Issuer, Audience = Audience, SigningKey = "unused-here" },
    };

    private static AppUser User(PlatformRole role) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = "issuer-test@nighttales.test",
        DisplayName = "issuer test",
        Role = role,
        SecurityStamp = Guid.NewGuid().ToString("N"),
    };

    private static string IssueKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        return key.ExportECPrivateKeyPem();
    }
}
