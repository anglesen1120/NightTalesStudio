using Microsoft.AspNetCore.Http;

namespace NightTales.Api.Http.Cookies;

/// <summary>
/// The <c>__Secure-nts_rt</c> refresh cookie (09 README BD3, 09.3.2-T1 Design): the Api — never a handler —
/// writes and clears it, with the flags the contract fixes.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth</c> plus <c>Max-Age</c> equal to the token's
/// remaining lifetime. <c>HttpOnly</c> keeps the token out of reach of script, <c>Secure</c> keeps it off
/// plain HTTP, <c>SameSite=Strict</c> keeps a cross-site form from carrying it, and the path keeps it off
/// every route that does not authenticate with it. The token is therefore never readable by the SPA, which
/// is why the access token travels in the response body instead.
/// </para>
/// <para>
/// The name comes from <c>Auth:Refresh:CookieName</c> and must keep the <c>__Secure-</c> prefix; the
/// start-up validation of the options refuses a name without it, because the prefix is what makes the
/// browser itself enforce the <c>Secure</c> flag (BD3).
/// </para>
/// </remarks>
public static class RefreshCookie
{
    /// <summary>The path of the auth group: the only place the refresh cookie is sent (BD3).</summary>
    public const string Path = Endpoints.AuthEndpoints.GroupPrefix;

    /// <summary>Writes the cookie with the token's remaining lifetime as its <c>Max-Age</c>.</summary>
    /// <param name="response">The response to write the header on.</param>
    /// <param name="name">The configured cookie name.</param>
    /// <param name="token">The plaintext refresh token; never logged.</param>
    /// <param name="expiresAt">The token's absolute expiry, from the scope's clock.</param>
    /// <param name="now">The scope's clock reading, so <c>Max-Age</c> never disagrees with <c>expiresAt</c>.</param>
    public static void Write(
        HttpResponse response,
        string name,
        string token,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var remaining = expiresAt - now;
        var options = Options();

        // An already-dead token cannot be written with a negative Max-Age; it becomes a session cookie for
        // the instant it reaches the client, and the next refresh refuses it either way.
        if (remaining > TimeSpan.Zero)
        {
            options.MaxAge = remaining;
        }

        response.Cookies.Append(name, token, options);
    }

    /// <summary>Expires the cookie, whatever it held (BD3: logout clears it).</summary>
    /// <param name="response">The response to write the header on.</param>
    /// <param name="name">The configured cookie name.</param>
    public static void Clear(HttpResponse response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        response.Cookies.Delete(name, Options());
    }

    /// <summary>
    /// The flags every write and every clear carries. <c>IsEssential</c> is set because this cookie is the
    /// session itself: a cookie-consent policy must never be able to drop it.
    /// </summary>
    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        IsEssential = true,
    };
}
