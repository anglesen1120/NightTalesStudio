namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The plaintext refresh token of a new family — returned to the caller exactly once, never stored
/// (09.3.2-T1 Design, BD3).
/// </summary>
/// <remarks>
/// <para>
/// Only <c>HashToken(Token)</c> reaches <c>refresh_tokens.token_hash</c>; the value here is what the Api
/// layer writes into the <c>__Secure-nts_rt</c> cookie. It must never be logged, audited or e-mailed
/// (BD16, 09.3.2-T1 Notes).
/// </para>
/// <para>
/// Like <see cref="IssuedAccessToken"/>, this record carries no Infrastructure type so the login flow
/// can hand it across the layer boundary (09 README D1/D2).
/// </para>
/// </remarks>
/// <param name="Token">The encoded token: 32 random bytes, base64url (BD3).</param>
/// <param name="FamilyId">The family this session belongs to; the access token's <c>sid</c> claim.</param>
/// <param name="ExpiresAt">The family's absolute expiry, from the scope's clock.</param>
public sealed record IssuedRefreshSession(string Token, Guid FamilyId, DateTimeOffset ExpiresAt);

/// <summary>
/// The successor token of one rotation: the presented token is consumed and this one replaces it
/// (09.3.2-T1 Design — rotation on every refresh, BD3).
/// </summary>
/// <param name="Token">The successor's encoded token; never stored, never logged (BD16).</param>
/// <param name="FamilyId">The family id, unchanged by a rotation.</param>
/// <param name="ExpiresAt">The successor's expiry: <c>min(family started + absolute, now + idle)</c>.</param>
public sealed record RotatedSession(string Token, Guid FamilyId, DateTimeOffset ExpiresAt);
