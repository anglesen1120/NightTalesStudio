namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The per-request half of the authorization URL (06 README D2): where Google sends the callback and
/// whether the flow carries the PKCE parameters.
/// </summary>
/// <remarks>
/// <see cref="Pkce"/> is the configured <c>YouTube:Pkce</c> flag, read once at connect time: it exists so
/// the F12 fallback is a configuration change and not a code edit, and it gates both the
/// <c>code_challenge</c>/<c>code_challenge_method</c> pair of the URL and the <c>code_verifier</c> of the
/// later exchange.
/// </remarks>
/// <param name="RedirectUri">The absolute callback URI Google must answer to (<c>YouTube:RedirectUri</c>).</param>
/// <param name="Pkce">
/// Whether this flow uses PKCE S256. When <see langword="false"/> the caller passes no code challenge and
/// the exchange sends no verifier, because Google refuses a verifier it never saw a challenge for.
/// </param>
public sealed record OAuthAuthorizationRequest(string RedirectUri, bool Pkce);
