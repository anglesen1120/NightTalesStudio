using ErrorOr;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// Signs and hashes the platform's tokens (09.3.2-T1 Design; 09 README BD3/BD4): the ES256 access token,
/// the enrolment-only token <c>/auth/login</c> mints for an unenrolled owner or admin, and the refresh
/// token's random/hashed pair.
/// </summary>
/// <remarks>
/// <para>
/// Declared here, not in <c>Application/Abstractions/Security/</c> as 09.3.2-T1's design sketches:
/// <see cref="AppUser"/> is an Infrastructure type (09.2.1-T1) and the Application assembly may not
/// reference Infrastructure (09 README D1/D2, enforced by
/// <c>AssemblyReferenceRules.Application_ReferencesOnlyAllowedPackages</c>), so a port that takes one
/// cannot compile there. <c>ICredentialVerifier</c> (09.3.1-T1) is the precedent, and every value this
/// port returns is an Application-side record (<see cref="IssuedAccessToken"/>) so the Api and the
/// handlers can consume it without an Infrastructure reference.
/// </para>
/// <para>
/// Registered as a singleton: the ECDsa key is loaded once at start-up, and the issuer holds no scoped
/// state. The ES256 algorithm is fixed — no configuration can select another one (W2-O3), and a missing
/// or malformed key is a start-up failure rather than a fallback.
/// </para>
/// </remarks>
public interface ITokenIssuer
{
    /// <summary>Issues the session's access token.</summary>
    /// <param name="user">The authenticated user; supplies <c>sub</c>, <c>role</c> and <c>stamp</c>.</param>
    /// <param name="familyId">The refresh family the token belongs to: the <c>sid</c> claim.</param>
    /// <param name="amr">How the caller proved who they are.</param>
    /// <param name="now">The scope's clock reading; <c>iat</c> and the <c>exp</c> base.</param>
    IssuedAccessToken IssueAccessToken(AppUser user, Guid familyId, AuthMethod amr, DateTimeOffset now);

    /// <summary>
    /// Issues the enrolment-only token of an owner/admin who has not enrolled in TOTP yet (BD4):
    /// <c>scope = mfa_enrol</c>, <c>amr = pwd</c>, 15 minutes, accepted only by <c>/me/mfa/setup|verify</c>.
    /// </summary>
    /// <param name="user">The owner or admin who must enrol.</param>
    /// <param name="now">The scope's clock reading.</param>
    IssuedAccessToken IssueEnrolmentToken(AppUser user, DateTimeOffset now);

    /// <summary>Creates one refresh token: 32 cryptographic random bytes, base64url (BD3).</summary>
    string CreateRefreshToken();

    /// <summary>
    /// Creates one opaque bearer secret: the same 32 cryptographic random bytes, base64url (BD3), for the
    /// values that are not refresh tokens — 09.3.2-T3's <c>mfaToken</c> and its pre-auth cookie, both of
    /// which are hashed with <see cref="HashToken"/> exactly like a refresh token.
    /// </summary>
    string CreateSecret();

    /// <summary>The only stored form of a refresh token: its SHA-256 as lowercase hex (BD3).</summary>
    /// <param name="token">The encoded token that travelled in the cookie.</param>
    string HashToken(string token);
}
