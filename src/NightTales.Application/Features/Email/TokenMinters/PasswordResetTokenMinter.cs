using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Email.TokenMinters;

/// <summary>
/// Mints the reset link of <c>password_reset</c> at send time (09 README §B10 WT17, WT30, 09.2.6-T3): it
/// creates the single-use token row through <see cref="IUserActionTokenService"/> and returns the
/// <c>#</c>-fragment link the template renders.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole reason the pipeline exists in this shape. <c>POST /auth/forgot-password</c> queues
/// ids and token-free values and answers identically whether the account exists or not (09.3.3-T1); a row
/// of <c>user_action_tokens</c> appears only when this minter runs, inside the Worker's job, and the
/// plaintext token exists only between the mint and the SMTP hand-off — the database stores its SHA-256.
/// A retry mints a new token and invalidates the previous one, so a mail the operator had to re-run never
/// leaves two live links behind.
/// </para>
/// <para>
/// The link is composed here from <see cref="IEmailLinkOrigin"/> and the <c>/reset-password#t=…</c> shape
/// of 09.3.3-T1's <c>AuthLinks.PasswordReset</c>. That helper lands with 09.3.3-T1's own work; this minter
/// composes the identical string, so switching to the helper later is a one-line change with no wire
/// effect (the token stays after the <c>#</c>, so it never reaches a server log or a referrer).
/// </para>
/// </remarks>
/// <param name="actionTokens">The token store of 09.3.3-T1.</param>
/// <param name="linkOrigin">The SPA origin every link is composed from.</param>
public sealed class PasswordResetTokenMinter(
    IUserActionTokenService actionTokens,
    IEmailLinkOrigin linkOrigin) : IEmailTokenMinter
{
    /// <summary>The template this minter belongs to; also its keyed-service key.</summary>
    public const string TemplateName = EmailTemplateCatalog.PasswordReset;

    /// <summary>The render key this minter owns.</summary>
    public const string LinkKey = "link";

    /// <summary>The SPA route that consumes the token from the URL fragment (BD3).</summary>
    public const string ResetPasswordPath = "/reset-password";

    /// <summary>
    /// The caller metadata of a Worker job: none. The token row of BD3 stores no IP and no user agent, and
    /// this issue happens outside any request — the request path merely queued the message (WT17).
    /// </summary>
    private static readonly RequestInfo NoRequest = new(null, null);

    /// <inheritdoc />
    public async Task<ErrorOr<IReadOnlyDictionary<string, string>>> MintAsync(
        Guid userId,
        string templateName,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (!string.Equals(templateName, TemplateName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{nameof(PasswordResetTokenMinter)}' mints '{TemplateName}' only, not '{templateName}'.");
        }

        var issued = await actionTokens
            .IssueAsync(userId, UserActionKind.PasswordReset, NoRequest, cancellationToken)
            .ConfigureAwait(false);

        if (issued.IsError)
        {
            return issued.Errors;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LinkKey] = BuildLink(issued.Value),
        };
    }

    /// <summary>
    /// The link of one reset token: origin, route, then the token after the <c>#</c> (BD3 — the SPA reads
    /// it, the server never sees it).
    /// </summary>
    /// <param name="token">The plaintext token the token service just issued.</param>
    private string BuildLink(string token) =>
        string.Concat(
            linkOrigin.Origin.GetLeftPart(UriPartial.Authority),
            ResetPasswordPath,
            "#t=",
            token);
}
