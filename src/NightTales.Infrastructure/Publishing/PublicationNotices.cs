using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The adapter of <see cref="IPublicationNotices"/> over the platform's one queueing port (06.2.2-T2, 06 README
/// D7/D8/D12): it turns a publication into the allow-listed values of <c>publication_consented</c> or
/// <c>publication_veto_notice</c> and hands them to <see cref="IEmailSender"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It queues, never sends.</b> <c>QueueAsync</c> writes the <c>email_messages</c> row and, through the outbox
/// interceptor, the <c>email_requested.v1</c> message of the caller's own transaction; it never calls
/// <c>SaveChangesAsync</c>, so the calling command's single save commits the publication and the message together
/// (09.2.6-T3 §Design). A failure — an unknown template, a key outside the allow-list — is returned to that
/// command, and the whole command fails: no publication row, no consent, no notice.
/// </para>
/// <para>
/// <b>Two explicit builders, one per template.</b> The templates have different allow-lists
/// (<c>publication_consented</c> has no description, tags or deadline; <c>publication_veto_notice</c> has no
/// approval line) and the sender refuses any key a template does not declare, so a shared builder would fail one
/// of the two the moment it emitted the other's key.
/// </para>
/// <para>
/// <b>Effective privacy, never the submitted one.</b> <see cref="Publication.Privacy"/> is already the effective
/// value (06.2.1-T1's pre-audit override stamped it at create, D12/YR20), so both messages report the row; the
/// exception is explained by <c>privacyNote</c>, which is built here from the <c>platform_payload</c>
/// <c>preAudit</c>/<c>requestedPrivacy</c> keys because the renderer cannot branch (09-backend-api README §6).
/// </para>
/// <para>
/// <b>What is never logged.</b> Template name, a short hash of the user id and the error code — never an address,
/// a token, a video title or a channel display name (06 README D17, BD16).
/// </para>
/// </remarks>
/// <param name="emailSender">The queueing port; it joins the caller's unit of work.</param>
/// <param name="db">The tenant-filtered write model, read here for one projection only.</param>
/// <param name="profiles">The account read of the owner's display name and <c>ui_locale</c> (09.3.4-T1's port).</param>
/// <param name="links">The origin every link inside a mail is composed from (<c>App:PublicOrigin</c>).</param>
/// <param name="logger">The id-only log lines of a failed enqueue (D17: no address, token, title or channel name).</param>
public sealed partial class PublicationNotices(
    IEmailSender emailSender,
    IAppDbContext db,
    IUserProfileReader profiles,
    IEmailLinkOrigin links,
    ILogger<PublicationNotices> logger) : IPublicationNotices
{
    /// <summary>The <c>platform_payload</c> flag 06.2.1-T1 writes when it replaced the submitted privacy.</summary>
    private const string PreAuditKey = "preAudit";

    /// <summary>The <c>platform_payload</c> key holding the privacy the owner submitted.</summary>
    private const string RequestedPrivacyKey = "requestedPrivacy";

    /// <summary>The request field a veto notice without a deadline answers on (the task file's YR1 row).</summary>
    private const string VetoEndsField = "vetoEndsAt";

    /// <summary>The SPA path of one publication, relative to the public origin.</summary>
    private const string PublicationsPath = "/publications/";

    /// <inheritdoc />
    public async Task<ErrorOr<Guid>> QueueVetoNoticeAsync(
        Publication publication,
        string channelDisplayName,
        string ownerDisplayName,
        Language locale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(channelDisplayName);
        ArgumentNullException.ThrowIfNull(ownerDisplayName);

        // YR1 gives every autopilot publication a window, and the notice exists to be read inside it: a row
        // without one has nothing to announce, and the minter needs the deadline to bound the token's lifetime.
        if (publication.VetoEndsAt is null)
        {
            return Errors.ValidationFailed(
                VetoEndsField,
                "An autopilot publication without a veto deadline cannot send a veto notice.");
        }

        var queued = await emailSender
            .QueueAsync(
                new EmailMessage(
                    publication.UserId,
                    EmailTemplateCatalog.PublicationVetoNotice,
                    locale,
                    VetoNoticeParams(publication, channelDisplayName, ownerDisplayName, locale)),
                cancellationToken)
            .ConfigureAwait(false);

        if (queued.IsError)
        {
            LogQueueFailed(
                EmailTemplateCatalog.PublicationVetoNotice,
                UserHash(publication.UserId),
                queued.FirstError.Code);
        }

        return queued;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> QueueConsentedAsync(
        Publication publication,
        ConsentSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);

        // The recipient and the render locale come from the account row, not from the caller (09 README D16): this
        // method's three callers all run after a consent and none of them has the profile in hand. A deleted
        // account is an error, never a message nobody can receive.
        var owner = await profiles.GetAsync(publication.UserId, cancellationToken).ConfigureAwait(false);

        if (owner is null)
        {
            return Errors.NotFound();
        }

        // The channel title, tenant-filtered through the same write model the caller uses and read as a projection:
        // the relation is never materialised and the publication itself (unsaved on the create path) is not read.
        var channelDisplayName = await db.ChannelConnections
            .Where(connection => connection.Id == publication.ChannelConnectionId)
            .Select(connection => connection.DisplayName)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (channelDisplayName is null)
        {
            return Errors.NotFound();
        }

        var queued = await emailSender
            .QueueAsync(
                new EmailMessage(
                    publication.UserId,
                    EmailTemplateCatalog.PublicationConsented,
                    owner.UiLocale,
                    ConsentedParams(
                        publication,
                        channelDisplayName,
                        owner.DisplayName,
                        ApprovalLine.For(source, owner.UiLocale),
                        source.ToWire(),
                        owner.UiLocale)),
                cancellationToken)
            .ConfigureAwait(false);

        if (queued.IsError)
        {
            LogQueueFailed(
                EmailTemplateCatalog.PublicationConsented,
                UserHash(publication.UserId),
                queued.FirstError.Code);

            return queued.Errors;
        }

        return Result.Success;
    }

    /// <summary>
    /// The allow-listed values of <c>publication_veto_notice</c> (06.2.2-T2 §Design): the preview, the deadline and
    /// the two links. <c>cancelUrl</c> is the minter's send-time output and is deliberately absent — a token never
    /// travels through <c>params</c> (09 README §B10 WT17/WT30).
    /// </summary>
    /// <param name="publication">The autopilot publication being announced.</param>
    /// <param name="channelDisplayName">The channel connection's title.</param>
    /// <param name="ownerDisplayName">The owner's display name.</param>
    /// <param name="locale">The render locale, which selects the privacy note's wording.</param>
    /// <returns>The values the catalog declares for the template.</returns>
    private Dictionary<string, string> VetoNoticeParams(
        Publication publication,
        string channelDisplayName,
        string ownerDisplayName,
        Language locale) =>
        new(StringComparer.Ordinal)
        {
            ["displayName"] = ownerDisplayName,
            ["channelTitle"] = channelDisplayName,
            ["videoTitle"] = publication.Title,
            ["description"] = publication.Description,
            ["tags"] = string.Join(", ", publication.Tags),
            ["privacy"] = publication.Privacy,
            ["privacyNote"] = PrivacyNote(publication, locale),
            ["scheduledAt"] = Utc(publication.PublishAt),
            // YR1 guarantees the deadline on an autopilot row, and QueueVetoNoticeAsync refused the row without
            // one: the non-null assertion is the guard above, not an assumption here.
            ["vetoEndsAt"] = Utc(publication.VetoEndsAt),
            ["publicationUrl"] = PublicationUrl(publication),
            // Producer key, never rendered: the minter reads it to scope the single-use token to this publication.
            ["publicationId"] = publication.Id.ToString(),
        };

    /// <summary>
    /// The allow-listed values of <c>publication_consented</c> (06.2.2-T2 §Design): the receipt of a consent.
    /// <c>consentSource</c> is queued for the payload and the audit trail and is not rendered; the source-dependent
    /// sentence is <paramref name="approvalLine"/>.
    /// </summary>
    /// <param name="publication">The publication that was consented.</param>
    /// <param name="channelDisplayName">The channel connection's title.</param>
    /// <param name="ownerDisplayName">The owner's display name.</param>
    /// <param name="approvalLine">The already-rendered, localized line of the consent source.</param>
    /// <param name="consentSource">The wire value of the consent source (<c>user</c>/<c>autopilot</c>).</param>
    /// <param name="locale">The render locale, which selects the privacy note's wording.</param>
    /// <returns>The values the catalog declares for the template.</returns>
    private Dictionary<string, string> ConsentedParams(
        Publication publication,
        string channelDisplayName,
        string ownerDisplayName,
        string approvalLine,
        string consentSource,
        Language locale) =>
        new(StringComparer.Ordinal)
        {
            ["displayName"] = ownerDisplayName,
            ["channelTitle"] = channelDisplayName,
            ["videoTitle"] = publication.Title,
            ["privacy"] = publication.Privacy,
            ["privacyNote"] = PrivacyNote(publication, locale),
            ["scheduledAt"] = Utc(publication.PublishAt),
            ["publicationUrl"] = PublicationUrl(publication),
            ["approvalLine"] = approvalLine,
            ["consentSource"] = consentSource,
        };

    /// <summary>
    /// The explanation a pre-audit row carries next to its privacy (06 README D12/YR20), or an empty string when the
    /// row kept the privacy the owner submitted.
    /// </summary>
    /// <remarks>
    /// The wording is permanent, not "until approval": once the client passed Google's audit the row stays private
    /// — only the next autopilot publication is released (YR20) — so a sentence promising a later release would be
    /// false. It is built here, in the owner's language, because a template may only substitute flat values.
    /// </remarks>
    /// <param name="publication">The row whose <c>platform_payload</c> may carry the pre-audit keys.</param>
    /// <param name="locale">The render locale.</param>
    /// <returns>The note, or <see cref="string.Empty"/>.</returns>
    private static string PrivacyNote(Publication publication, Language locale)
    {
        if (!TryReadPreAudit(publication.PlatformPayloadJson, out var requestedPrivacy))
        {
            return string.Empty;
        }

        // The writer of the payload always stores the submitted privacy with the flag; a payload that lost the key
        // is described by the row's own value rather than by an empty quote.
        var requested = string.IsNullOrWhiteSpace(requestedPrivacy) ? publication.Privacy : requestedPrivacy;

        return locale is Language.En
            ? $"(will be published as Private because the project has not passed Google's audit; you selected {requested})"
            : $"(sẽ được đăng ở chế độ Riêng tư vì dự án chưa vượt qua đợt kiểm duyệt của Google; bạn đã chọn {requested})";
    }

    /// <summary>
    /// Reads the two <c>platform_payload</c> keys 06.2.1-T1 writes (06 README D12). Absent, malformed or of the
    /// wrong JSON kind reads back as "no pre-audit stamp", never as an error.
    /// </summary>
    /// <remarks>
    /// The writer's reader (<c>PublicationPlatformPayload</c>, 06.2.1-T1) is internal to the Application assembly,
    /// so the two key names are spelled again here: this adapter reads the document, it never writes it, and the
    /// create path — the only writer of these two keys — stamps them before either notice is queued.
    /// </remarks>
    /// <param name="json">The <c>platform_payload</c> column, or <see langword="null"/>.</param>
    /// <param name="requestedPrivacy">The submitted privacy, or <see langword="null"/> when the payload lost it.</param>
    /// <returns>Whether the row carries the pre-audit stamp.</returns>
    private static bool TryReadPreAudit(string? json, out string? requestedPrivacy)
    {
        requestedPrivacy = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(PreAuditKey, out var preAudit)
                || preAudit.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty(RequestedPrivacyKey, out var requested)
                && requested.ValueKind == JsonValueKind.String)
            {
                requestedPrivacy = requested.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The publication page of a message: the public origin and the row's id.
    /// </summary>
    /// <remarks>
    /// [INFERENCE] The SPA route of the publication page is not fixed yet (10.7.x owns it); this is the pattern of
    /// the existing static links (<c>ConnectChannelConnectionHandler.SecurityLink</c>), and 06.2.2-T4 reuses this
    /// same origin.
    /// </remarks>
    /// <param name="publication">The row the message is about.</param>
    /// <returns>The absolute URL.</returns>
    private string PublicationUrl(Publication publication) =>
        $"{links.Origin.GetLeftPart(UriPartial.Authority)}{PublicationsPath}{publication.Id}";

    /// <summary>
    /// An instant as ISO-8601 UTC — the form both templates label <c>UTC</c> — or an empty string when the row has
    /// none (the templates read "as soon as it is ready" for a blank <c>scheduledAt</c>).
    /// </summary>
    /// <param name="value">The instant, or <see langword="null"/>.</param>
    /// <returns>The round-tripping UTC text, or <see cref="string.Empty"/>.</returns>
    private static string Utc(DateTimeOffset? value) =>
        value is null
            ? string.Empty
            : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A short correlation hash of a user id, so a log line names no account (BD16).</summary>
    /// <remarks>The recipe is <c>PasswordResetService.UserHash</c>'s: SHA-256 over the id, 16 hexadecimal digits.</remarks>
    /// <param name="userId">The owner of the publication.</param>
    /// <returns>The correlation hash.</returns>
    private static string UserHash(Guid userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId.ToString())))[..16];

    [LoggerMessage(
        EventId = 6530,
        Level = LogLevel.Error,
        Message = "publication notice enqueue failed: template={Template} userHash={UserHash} code={Code}")]
    private partial void LogQueueFailed(string template, string userHash, string code);
}
