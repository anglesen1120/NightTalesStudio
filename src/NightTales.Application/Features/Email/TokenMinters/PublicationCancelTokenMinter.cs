using System.Globalization;
using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Auth.Password;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Email.TokenMinters;

/// <summary>
/// Mints the cancel link of <c>publication_veto_notice</c> at send time (06 README D7, YR8; 09 README §B10
/// WT17, WT30): it creates a single-use, publication-scoped token row through
/// <see cref="IUserActionTokenService"/> and returns the <c>#</c>-fragment link the template renders.
/// </summary>
/// <remarks>
/// <para>
/// The veto notice is queued before the window closes and carries no capability: the token row appears only
/// when this minter runs, inside <c>SendEmailJob</c>, and the plaintext exists only between the mint and the
/// SMTP hand-off — the database stores its SHA-256. A retry mints a new row and invalidates the previous one
/// for the same <c>(user, kind, subject)</c>, so a re-sent notice never leaves two live links behind.
/// </para>
/// <para>
/// <b>Lifetime.</b> The link may not outlive the veto window it belongs to and never exceeds 48 hours:
/// <c>expiresAt = min(vetoEndsAt, now + 48h)</c> (YR8). The window and the deadline both come from the
/// producer's queued parameters — <c>publicationId</c> and <c>vetoEndsAt</c> — so no extra query is needed
/// for a publication that may still be unsaved when the notice is queued.
/// </para>
/// <para>
/// <b>A closed window mints nothing.</b> When the deadline has already passed by the time the job runs
/// (delivery was slower than a short veto window), the minter answers an error instead of a token: a link
/// that is expired on arrival can only ever produce the route's generic 400, and this job must not mail a
/// dead capability. <c>SendEmailJob</c> records the error's code on the row, and the catalogue has no closer
/// code for it than <c>email_send_failed</c> (the same reason that job gives for its own catch-all).
/// </para>
/// </remarks>
/// <param name="actionTokens">The token store of 09.3.3-T1.</param>
/// <param name="linkOrigin">The SPA origin every link is composed from.</param>
/// <param name="clock">The platform clock the 48-hour cap is measured against.</param>
public sealed class PublicationCancelTokenMinter(
    IUserActionTokenService actionTokens,
    IEmailLinkOrigin linkOrigin,
    IClock clock) : IEmailTokenMinter
{
    /// <summary>The template this minter belongs to; also its keyed-service key.</summary>
    public const string TemplateName = EmailTemplateCatalog.PublicationVetoNotice;

    /// <summary>The render key this minter owns.</summary>
    public const string CancelUrlKey = "cancelUrl";

    /// <summary>The producer parameter that names the publication the token is scoped to.</summary>
    public const string PublicationIdParameter = "publicationId";

    /// <summary>The producer parameter that carries the veto deadline in ISO-8601.</summary>
    public const string VetoEndsAtParameter = "vetoEndsAt";

    /// <summary>The hard cap on a cancel link's life, however long the veto window is (YR8).</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(48);

    /// <summary>
    /// The caller metadata of a Worker job: none. The token row stores no IP and no user agent, and this mint
    /// happens outside any request — the request path merely queued the message (WT17).
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

        // A minter is resolved by its template name, so another template reaching this one is a caller bug;
        // answering the empty set keeps the job's contract ("the values this template mints") total, and the
        // catalog decides which templates are token-bearing in the first place.
        if (!string.Equals(templateName, TemplateName, StringComparison.Ordinal))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (!parameters.TryGetValue(PublicationIdParameter, out var rawPublicationId)
            || !Guid.TryParse(rawPublicationId, out var publicationId)
            || publicationId == Guid.Empty)
        {
            return MintFailed($"the queued '{PublicationIdParameter}' parameter is missing or not a publication id");
        }

        if (!parameters.TryGetValue(VetoEndsAtParameter, out var rawVetoEndsAt)
            || !DateTimeOffset.TryParse(
                rawVetoEndsAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var vetoEndsAt))
        {
            return MintFailed($"the queued '{VetoEndsAtParameter}' parameter is missing or not an ISO-8601 instant");
        }

        var now = clock.UtcNow;
        var cappedAt = now + MaximumLifetime;
        var expiresAt = vetoEndsAt < cappedAt ? vetoEndsAt : cappedAt;

        if (expiresAt <= now)
        {
            return MintFailed("the veto window of this publication has already closed");
        }

        var issued = await actionTokens
            .IssueAsync(
                userId,
                UserActionKind.PublicationCancel,
                publicationId,
                expiresAt,
                NoRequest,
                cancellationToken)
            .ConfigureAwait(false);

        if (issued.IsError)
        {
            return issued.Errors;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CancelUrlKey] = AuthLinks.PublicationCancel(linkOrigin.Origin, publicationId, issued.Value),
        };
    }

    /// <summary>
    /// A veto notice that cannot carry a live link. The message names the parameter or the state and never a
    /// value: the queued parameters are not sensitive, but the link they lead to is (09 README BD16).
    /// </summary>
    /// <param name="reason">What is wrong, without quoting any parameter value.</param>
    private static Error MintFailed(string reason) =>
        Error.Failure(
            ErrorCodes.EmailSendFailed,
            $"The cancel link of the publication veto notice was not minted: {reason}.");
}
