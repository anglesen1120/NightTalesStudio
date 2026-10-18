using Microsoft.AspNetCore.Http;

namespace NightTales.Api.Http.Cookies;

/// <summary>
/// The <c>__Host-nts_oauth</c> browser-binding cookie of 06.1.2-T1 (06 README D3, YR10): the Api — never
/// a handler — writes it for a connect and clears it on every callback outcome.
/// </summary>
/// <remarks>
/// <para>
/// The connect mints a 32-byte nonce next to the single-use <c>state</c> and returns it only as this
/// cookie; the callback must present it, and its <c>ITokenIssuer.HashToken</c> value must equal
/// <c>oauth_states.browser_nonce_hash</c>. The state alone would authenticate a flow the user started,
/// not the browser that started it, so both bindings are required and neither alone completes a connect
/// (F01).
/// </para>
/// <para>
/// <c>__Host-</c> requires <c>Secure</c>, <c>Path=/</c> and no <c>Domain</c>, and the browser itself
/// enforces the triple — which is why the prefix is part of the name instead of configuration.
/// <c>SameSite=Lax</c> is required rather than cosmetic: the callback is a cross-site top-level GET from
/// Google, and <c>Strict</c> would drop the cookie on exactly that request.
/// </para>
/// <para>
/// The value is a credential for the ten minutes the state lives: it is never serialised into a response
/// body, never interpolated into a <c>Location</c> header and never logged (09 README D17).
/// </para>
/// </remarks>
public static class OAuthNonceCookie
{
    /// <summary>The YR10 cookie name; <c>__Host-</c> is part of the contract, not a default.</summary>
    public const string Name = "__Host-nts_oauth";

    /// <summary>The path the cookie is scoped to; the <c>__Host-</c> prefix requires exactly <c>/</c>.</summary>
    public const string Path = "/";

    /// <summary>The cookie's plaintext value, or null when the request carried none.</summary>
    /// <param name="request">The request to read the cookie from.</param>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Cookies[Name];

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Writes the cookie with the state's remaining lifetime as its <c>Max-Age</c>.</summary>
    /// <param name="response">The response to write the header on.</param>
    /// <param name="nonce">The plaintext nonce; never serialised and never logged.</param>
    /// <param name="expiresAt">When the state the nonce is bound to dies, from the handler.</param>
    /// <param name="now">The scope's clock reading, so <c>Max-Age</c> never disagrees with <paramref name="expiresAt"/>.</param>
    public static void Write(HttpResponse response, string nonce, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var remaining = expiresAt - now;
        var options = Options();

        // A state that is already dead cannot be written with a negative Max-Age; the cookie becomes a
        // session cookie for the instant it reaches the client, and the callback refuses it either way
        // (the claiming UPDATE matches no live row).
        if (remaining > TimeSpan.Zero)
        {
            // Rounded up to a whole second: the handler minted the state a few microseconds before this
            // route read its own clock, so a truncated remainder would write a Max-Age one second short of
            // the contract's 600 (the two reads are the same scoped clock, 10 minutes apart by exactly the
            // configured `YouTube:OAuthStateMinutes`). One second of extra cookie life is inert — the
            // state's own expiry is what the callback claims against.
            options.MaxAge = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        }

        response.Cookies.Append(Name, nonce, options);
    }

    /// <summary>
    /// Expires the cookie, whatever it held (YR10): the state is single use, so after a callback — the one
    /// that succeeded and each of the four failures — the browser must not keep presenting a nonce that can
    /// never complete a connect again.
    /// </summary>
    /// <param name="response">The response to write the header on.</param>
    /// <remarks>
    /// The header is written by hand rather than through <c>ResponseCookies.Delete</c>, which emits only a
    /// past <c>Expires</c>: the contract is an explicit <c>Max-Age=0</c>, and the past instant is kept as
    /// the second line for a client that prefers absolute expiry.
    /// </remarks>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var options = Options();
        options.MaxAge = TimeSpan.Zero;
        options.Expires = DateTimeOffset.UnixEpoch;

        response.Cookies.Append(Name, string.Empty, options);
    }

    /// <summary>
    /// The flags every write and every clear carries. <c>IsEssential</c> is set because the cookie is part
    /// of the sign-in the user just asked for, so a cookie-consent policy must never be able to drop it.
    /// </summary>
    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = Path,
        IsEssential = true,
    };
}
