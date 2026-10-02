namespace NightTales.Application.Features.Auth.Password;

/// <summary>
/// The links the auth e-mails carry (09 README BD3/WF11, 09.3.3-T1 Design): the SPA origin, the route and
/// then the single-use token <b>after the <c>#</c></b>.
/// </summary>
/// <remarks>
/// <para>
/// The fragment placement is the contract, not a style: a browser never sends the part after <c>#</c> to a
/// server, so the token cannot reach an access log, a proxy log or the <c>Referer</c> header of the page
/// that follows. <c>Referrer-Policy: no-referrer</c> (09.9.4-T1) is the second layer.
/// </para>
/// <para>
/// The helper takes the origin as an argument instead of resolving <c>App:PublicOrigin</c> itself, so the
/// Application layer stays free of configuration and the send-time minter keeps using the
/// <c>IEmailLinkOrigin</c> port it already has.
/// </para>
/// </remarks>
public static class AuthLinks
{
    /// <summary>The SPA route that reads a reset token from its URL fragment (BD3).</summary>
    public const string ResetPasswordPath = "/reset-password";

    /// <summary>The SPA route that reads a verification token from its URL fragment (BD3, S phase).</summary>
    public const string VerifyEmailPath = "/verify-email";

    /// <summary>The password-reset link of one token.</summary>
    /// <param name="origin">The platform's public origin; its authority is what the link uses.</param>
    /// <param name="token">The plaintext token, placed after the <c>#</c>.</param>
    public static string PasswordReset(Uri origin, string token) => Link(origin, ResetPasswordPath, token);

    /// <summary>The e-mail-verification link of one token (09.3.1-T3 consumes it).</summary>
    /// <param name="origin">The platform's public origin; its authority is what the link uses.</param>
    /// <param name="token">The plaintext token, placed after the <c>#</c>.</param>
    public static string VerifyEmail(Uri origin, string token) => Link(origin, VerifyEmailPath, token);

    /// <summary>origin + path + <c>#t=</c> + token, the one shape both links share.</summary>
    private static string Link(Uri origin, string path, string token)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return string.Concat(origin.GetLeftPart(UriPartial.Authority), path, "#t=", token);
    }
}
