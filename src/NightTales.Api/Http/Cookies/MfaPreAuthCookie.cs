using Microsoft.AspNetCore.Http;

namespace NightTales.Api.Http.Cookies;

/// <summary>
/// The <c>__Secure-nts_mfa</c> pre-auth cookie of 09.3.2-T3 (09 README BD4, 09.3-auth plan §5): the Api —
/// never a handler — writes and clears it, with the same flags as the refresh cookie.
/// </summary>
/// <remarks>
/// <para>
/// The password step returns two secrets and they travel on two channels: the single-use
/// <c>mfaToken</c> in the 401 body, and this cookie's value. A completion must present <em>both</em>, so
/// a body captured from a log or a proxy is useless without the browser's cookie, and a stolen cookie is
/// useless without the body.
/// </para>
/// <para>
/// <c>HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth</c>, and <c>Max-Age</c> equal to the
/// challenge's remaining lifetime (<c>Auth:Mfa:ChallengeMinutes</c>, BD4: five minutes). The name is a
/// constant rather than configuration because the <c>__Secure-</c> prefix is what makes the browser
/// itself enforce <c>Secure</c>, and the contract is frozen for the SPA
/// (10.3.3's C2: <c>__Secure-nts_mfa</c>, 5 min).
/// </para>
/// </remarks>
public static class MfaPreAuthCookie
{
    /// <summary>The BD4 cookie name; it must keep the <c>__Secure-</c> prefix.</summary>
    public const string Name = "__Secure-nts_mfa";

    /// <summary>The path of the auth group: the only place the cookie is sent (BD4).</summary>
    public const string Path = Endpoints.AuthEndpoints.GroupPrefix;

    /// <summary>The cookie's plaintext value, or null when the request carried none.</summary>
    /// <param name="request">The request to read the cookie from.</param>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Cookies[Name];

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Writes the cookie with the challenge's remaining lifetime as its <c>Max-Age</c>.</summary>
    /// <param name="response">The response to write the header on.</param>
    /// <param name="value">The plaintext pre-auth secret; never logged.</param>
    /// <param name="expiresAt">The challenge's expiry, from the scope's clock.</param>
    /// <param name="now">The scope's clock reading, so <c>Max-Age</c> never disagrees with <c>expiresAt</c>.</param>
    public static void Write(HttpResponse response, string value, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var remaining = expiresAt - now;
        var options = Options();

        // A challenge that is already dead cannot be written with a negative Max-Age; it becomes a session
        // cookie for the instant it reaches the client, and the completion refuses it either way.
        if (remaining > TimeSpan.Zero)
        {
            options.MaxAge = remaining;
        }

        response.Cookies.Append(Name, value, options);
    }

    /// <summary>
    /// Expires the cookie. The completed challenge is dead, so leaving its cookie behind would only make
    /// the client resend a spent secret (BD4).
    /// </summary>
    /// <param name="response">The response to write the header on.</param>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(Name, Options());
    }

    /// <summary>
    /// The flags every write and every clear carries, matching <see cref="RefreshCookie"/>: this cookie is
    /// part of the sign-in the user just asked for, so a cookie-consent policy must not be able to drop it.
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
