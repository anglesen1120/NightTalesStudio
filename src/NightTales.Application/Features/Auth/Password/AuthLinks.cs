namespace NightTales.Application.Features.Auth.Password;

/// <summary>
/// The links the single-use-token e-mails carry (09 README BD3/WF11, 09.3.3-T1 Design; the publication-cancel
/// member is 06 CR-6): the SPA origin, the route and then the token <b>after the <c>#</c></b>.
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

    /// <summary>
    /// The SPA route that reads a publication-cancel token from its URL fragment (06 CR-6, 06.2.2-T2). The
    /// route also reads the publication id from the fragment, so a cancel link names its object without a
    /// second token and the page can explain what it is about to cancel.
    /// </summary>
    public const string PublicationCancelPath = "/publications/cancel";

    /// <summary>The password-reset link of one token.</summary>
    /// <param name="origin">The platform's public origin; its authority is what the link uses.</param>
    /// <param name="token">The plaintext token, placed after the <c>#</c>.</param>
    public static string PasswordReset(Uri origin, string token) => Link(origin, ResetPasswordPath, token);

    /// <summary>The e-mail-verification link of one token (09.3.1-T3 consumes it).</summary>
    /// <param name="origin">The platform's public origin; its authority is what the link uses.</param>
    /// <param name="token">The plaintext token, placed after the <c>#</c>.</param>
    public static string VerifyEmail(Uri origin, string token) => Link(origin, VerifyEmailPath, token);

    /// <summary>
    /// The cancel-by-link link of one publication (06 CR-6, 06.2.2-T2): the publication id and then the
    /// single-use token, both <b>after</b> the <c>#</c>.
    /// </summary>
    /// <param name="origin">The platform's public origin; its authority is what the link uses.</param>
    /// <param name="publicationId">The publication the token authorizes cancelling.</param>
    /// <param name="token">The plaintext token, placed after the <c>#</c>.</param>
    public static string PublicationCancel(Uri origin, Guid publicationId, string token)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return string.Concat(
            origin.GetLeftPart(UriPartial.Authority),
            PublicationCancelPath,
            "#p=",
            publicationId.ToString("D"),
            "&t=",
            token);
    }

    /// <summary>origin + path + <c>#t=</c> + token, the one shape the reset and verify links share.</summary>
    private static string Link(Uri origin, string path, string token)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return string.Concat(origin.GetLeftPart(UriPartial.Authority), path, "#t=", token);
    }
}
