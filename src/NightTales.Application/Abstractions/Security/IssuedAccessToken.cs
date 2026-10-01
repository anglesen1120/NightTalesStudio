namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// One signed ES256 access token and the facts the DTO needs (09.3.2-T1 Design): the encoded token, the
/// instant it expires and the key id it was signed with.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExpiresAt"/> is computed from the scope's <c>IClock</c> (09 README D3) and is the value the
/// login/refresh response returns as <c>expiresAt</c> (09.3-auth plan §5); nothing has to decode the
/// token to learn when it dies.
/// </para>
/// <para>
/// The record is deliberately free of <c>AppUser</c> and of every Infrastructure type, so it can travel
/// between the Api, the handlers and the Infrastructure-side issuer (09 README D1/D2 — the Application
/// assembly may not reference Infrastructure).
/// </para>
/// </remarks>
/// <param name="Token">The encoded JWT; never logged and never placed in a URL (BD16).</param>
/// <param name="ExpiresAt">The <c>exp</c> instant, from the scope's clock.</param>
/// <param name="Kid">The signing key id, <c>SHA256(SubjectPublicKeyInfo)[..16]</c> (BD3).</param>
public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt, string Kid);
