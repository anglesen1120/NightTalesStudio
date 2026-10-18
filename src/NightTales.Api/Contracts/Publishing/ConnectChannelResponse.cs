namespace NightTales.Api.Contracts.Publishing;

/// <summary>
/// The body of <c>POST /api/v1/channels/{platform}/connect</c> (06 README D3, 06.1 connect plan §5.1): the
/// Google authorization URL the browser must follow, and the instant the single-use state inside it dies.
/// </summary>
/// <remarks>
/// The handler's own response carries one more member — the <c>__Host-nts_oauth</c> nonce of YR10 — and it
/// is deliberately absent here: the nonce travels only in a cookie, the wire body is exactly these two
/// members, and nothing may serialise the third (YR10).
/// </remarks>
/// <param name="AuthorizationUrl">
/// The <c>accounts.google.com</c> URL carrying the client id, the redirect URI, the two scopes, the
/// <c>state</c> and, unless <c>YouTube:Pkce</c> is off, the S256 code challenge.
/// </param>
/// <param name="ExpiresAt">When the <c>state</c> inside <paramref name="AuthorizationUrl"/> stops being usable, in UTC.</param>
public sealed record ConnectChannelResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);
