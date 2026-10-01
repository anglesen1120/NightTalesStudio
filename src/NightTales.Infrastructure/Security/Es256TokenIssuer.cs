using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The ES256 issuer of 09.3.2-T1 (09 README BD3, W2-O3): every access token and every enrolment-only
/// token, signed with the one key the host loaded at start-up.
/// </summary>
/// <remarks>
/// <para>
/// The claims are exactly BD3's: <c>sub</c>, <c>role</c> (snake_case), <c>sid</c> (the refresh family),
/// <c>amr</c>, <c>stamp</c> (09.3.2-T2 reads it), <c>jti</c>, <c>iat</c>, <c>exp</c>, plus
/// <c>iss</c>/<c>aud</c> and the enrolment token's <c>scope</c>. Lifetimes come from
/// <see cref="JwtOptions.AccessSection.LifetimeFor"/>: 5 minutes for an owner/admin, 15 for a user.
/// </para>
/// <para>
/// A token is never logged and never placed in a URL (BD16): nothing in this type writes to a logger, and
/// the only value it returns is the record the caller asked for.
/// </para>
/// <para>
/// A singleton, like the key it holds: it keeps only the immutable options snapshot of the process, so a
/// mid-flight configuration change cannot make two tokens from the same session disagree about
/// <c>iss</c>/<c>aud</c>.
/// </para>
/// </remarks>
/// <param name="signingKey">The key loaded at start-up, already proven to be a P-256 private key.</param>
/// <param name="options">The bound <c>Auth:Jwt</c>/<c>Auth:Access</c> configuration.</param>
public sealed class Es256TokenIssuer(JwtSigningKey signingKey, IOptions<JwtOptions> options) : ITokenIssuer
{
    /// <summary>The number of bytes of entropy in one refresh token (BD3).</summary>
    public const int RefreshTokenBytes = 32;

    /// <summary>The number of bytes of entropy in one <c>jti</c>.</summary>
    public const int JtiBytes = 16;

    /// <summary>The <c>scope</c> value of an enrolment-only token (BD4).</summary>
    public const string EnrolmentScope = "mfa_enrol";

    /// <summary>How long an enrolment-only token lives (BD4: 15 minutes).</summary>
    public static readonly TimeSpan EnrolmentLifetime = TimeSpan.FromMinutes(15);

    private readonly JsonWebTokenHandler _handler = new();

    /// <inheritdoc />
    public IssuedAccessToken IssueAccessToken(AppUser user, Guid familyId, AuthMethod amr, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = user.Id.ToString(),
            ["role"] = RoleClaim(user),
            ["sid"] = familyId.ToString(),
            ["amr"] = amr.ToAmrClaim(),
            ["stamp"] = user.SecurityStamp ?? string.Empty,
            ["jti"] = NewJti(),
        };

        return Issue(user, claims, options.Value.Access.LifetimeFor(user.Role), now);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The token carries no <c>sid</c>: it belongs to no refresh family, and the routes it unlocks are the
    /// two enrolment ones only (BD4).
    /// </remarks>
    public IssuedAccessToken IssueEnrolmentToken(AppUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = user.Id.ToString(),
            ["role"] = RoleClaim(user),
            ["amr"] = AuthMethod.Pwd.ToAmrClaim(),
            ["scope"] = EnrolmentScope,
            ["stamp"] = user.SecurityStamp ?? string.Empty,
            ["jti"] = NewJti(),
        };

        return Issue(user, claims, EnrolmentLifetime, now);
    }

    /// <inheritdoc />
    public string CreateRefreshToken()
        => Base64Url(RandomNumberGenerator.GetBytes(RefreshTokenBytes));

    /// <inheritdoc />
    public string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private IssuedAccessToken Issue(
        AppUser user,
        Dictionary<string, object> claims,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        var jwt = options.Value.Jwt;
        var expiresAt = now + lifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            // NotBefore comes from the injected clock too: left unset, the library would stamp the *real*
            // current time, which is exactly the hidden second clock 09 README D3 forbids — and it would put
            // `nbf` after `exp` for any caller whose clock is not the system's.
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            // One key per signature: the library disposes the key a signature provider was built from, so
            // shared credentials would sign once and throw afterwards (see JwtSigningKey).
            SigningCredentials = signingKey.CreateSigningCredentials(),
        };

        return new IssuedAccessToken(_handler.CreateToken(descriptor), expiresAt, signingKey.KeyId);
    }

    /// <summary>The snake_case <c>role</c> claim, the same spelling the D8 stored-value convention uses.</summary>
    private static string RoleClaim(AppUser user) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(user.Role.ToString());

    private static string NewJti() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(JtiBytes));

    /// <summary>base64url without padding, the encoding BD3 names for both the refresh token and its hash input.</summary>
    private static string Base64Url(byte[] bytes) => Convert
        .ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
