using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The two e-mails a publication event sends (06.2.2-T2, 06 README D7/D8): the receipt of every consent and the
/// veto notice of an autopilot publication.
/// </summary>
/// <remarks>
/// <para>
/// It is a port over <c>IEmailSender.QueueAsync</c>, not a second enqueue mechanism: the implementation resolves
/// the template's allow-listed values from the aggregate, hands them to the standard sender and returns whatever
/// it answered. <c>QueueAsync</c> never commits on its own, so every call joins the calling command's unit of
/// work — the row and its <c>email_requested.v1</c> event commit with the publication write in the handler's
/// single <c>SaveChangesAsync</c>, and a queueing failure fails that whole command (§06.2.2-T2 design, YR9).
/// </para>
/// <para>
/// <b>What is never held here.</b> No value of either message carries an e-mail address, a token or a link with
/// one: the two links are plain pages (<c>publicationUrl</c>) and the cancel link of the veto notice is the
/// minter's own send-time key, so it never travels through a queued message, an outbox payload or a log line
/// (09 README §B10 WT17/WT30, D17).
/// </para>
/// </remarks>
public interface IPublicationNotices
{
    /// <summary>
    /// Queues the veto notice of an autopilot publication (06 README D7): the preview of the video, its veto
    /// deadline and the links an owner needs inside the window.
    /// </summary>
    /// <remarks>
    /// The caller has the entity in hand — the create path calls this on the tracked, still-unsaved row — so the
    /// two denormalized names come as parameters and no related row is read again.
    /// </remarks>
    /// <param name="publication">The publication being created; its <c>veto_ends_at</c> must be set (YR1).</param>
    /// <param name="channelDisplayName">The channel connection's title, already loaded by the caller.</param>
    /// <param name="ownerDisplayName">The owner's display name, already loaded by the caller.</param>
    /// <param name="locale">The owner's <c>ui_locale</c>, as the one locale enum (09 README D16).</param>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <returns>
    /// The <c>email_messages.id</c> the caller records on the row (<c>platform_payload.vetoNoticeEmailId</c>,
    /// 06.2.2-T3 polls it), or the sender's error.
    /// </returns>
    Task<ErrorOr<Guid>> QueueVetoNoticeAsync(
        Publication publication,
        string channelDisplayName,
        string ownerDisplayName,
        Language locale,
        CancellationToken cancellationToken);

    /// <summary>
    /// Queues the receipt of a consent (06 README D7, YR9): channel title, video title, the effective privacy,
    /// the scheduled time and the link to the publication page, whichever source consented.
    /// </summary>
    /// <remarks>
    /// The application port's contract: the implementation resolves the channel title and the owner's display name
    /// and locale itself, because this path has three callers (the consent route, 06.2.2-T3's autopilot consent job
    /// and 06.2.2-T4's approve-now) and repeating the lookup at each of them is worse than doing it once here.
    /// </remarks>
    /// <param name="publication">The publication that was just consented.</param>
    /// <param name="source">Who consented; it selects the localized line the message carries (YR9).</param>
    /// <param name="cancellationToken">Cancels the queueing.</param>
    /// <returns><c>Success</c>, or the sender's error (or <c>not_found</c> when a related row is gone).</returns>
    Task<ErrorOr<Success>> QueueConsentedAsync(
        Publication publication,
        ConsentSource source,
        CancellationToken cancellationToken);
}
