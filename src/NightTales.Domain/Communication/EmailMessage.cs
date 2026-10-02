using NightTales.Domain.Common;

namespace NightTales.Domain.Communication;

/// <summary>
/// One e-mail the platform owes a user (09 README BD9, 09.2.6-T3, table <c>email_messages</c>): the
/// aggregate a feature's command queues through <c>IEmailSender</c> and the row <c>SendEmailJob</c>
/// records its attempts in.
/// </summary>
/// <remarks>
/// <para>
/// It owns the render input. The outbox row that carries the event is purged after seven days
/// (<c>OutboxCleanupJob</c>, 09.2.4 plan L49) while a Hangfire retry can run later, so
/// <see cref="ParamsJson"/> is stored here and cleared once the message is sent (09 README §B10 WT17:
/// a row that reached <c>sent</c> keeps no render values at all).
/// </para>
/// <para>
/// The stored values are the producer's allow-listed, token-free ones only. A single-use token — the
/// reset link of <c>password_reset</c> — is minted at send time inside <c>SendEmailJob</c> and lives
/// only in the rendered SMTP body, so it is never part of <see cref="ParamsJson"/> and never part of the
/// outbox payload (09 README §B10 WT17, WT30).
/// </para>
/// <para>
/// The row carries no address: <see cref="ToHash"/> is the hex SHA-256 of the recipient with the
/// platform pepper, which is what correlates rows and what appears in logs (09 README BD16); the live
/// address is read from <c>users.email</c> when the job runs, so an address change between queue and send
/// is followed instead of duplicated [DECISION].
/// </para>
/// <para>
/// It implements <see cref="IUserOwned"/>, so the 09 README D10 tenant filter applies to every read: the
/// job resolves the owner through <c>ISystemWorkQuery</c> and then pins its scope, and
/// <c>IEmailDeliveryStatus.GetAsync</c> answers 404 for another user's message with no hand-rolled
/// predicate.
/// </para>
/// </remarks>
public sealed class EmailMessage : AggregateRoot, IUserOwned
{
    private EmailMessage()
    {
    }

    /// <inheritdoc />
    public Guid UserId { get; private init; }

    /// <summary>The template name, a key of the Application's <c>EmailTemplateCatalog</c>.</summary>
    public string Template { get; private init; } = string.Empty;

    /// <summary>
    /// The locale the message renders in (09 README D16: the same <see cref="Language"/> the owner's
    /// <c>ui_locale</c> carries — there is no second enum for the template locale).
    /// </summary>
    public Language Locale { get; private init; }

    /// <summary>The hex SHA-256 of the recipient address with the pepper; the only address form stored here.</summary>
    public string ToHash { get; private init; } = string.Empty;

    /// <summary>
    /// The producer's allow-listed render values as JSON, or <see langword="null"/> once the message was
    /// sent. Mapped to the <c>params</c> column (a required addition to 00 §6's row, 09.2.6-T3 Notes).
    /// </summary>
    public string? ParamsJson { get; private set; }

    /// <summary>The delivery state; see <see cref="EmailStatus"/>.</summary>
    public EmailStatus Status { get; private set; }

    /// <summary>How many times the job started an attempt; the job increments it before it sends.</summary>
    public int Attempts { get; private set; }

    /// <summary>The id the SMTP message carried, set on success.</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>Why the last attempt failed, set on failure; a code, never a message (09 README D17).</summary>
    public string? LastErrorCode { get; private set; }

    /// <summary>When the producer queued the message, from its scope's clock.</summary>
    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>When the SMTP server accepted the message, or <see langword="null"/>.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>
    /// Records a queued message and raises <c>email_requested.v1</c>, which the outbox interceptor writes
    /// in the caller's own <c>SaveChanges</c> (09 README D11).
    /// </summary>
    /// <param name="userId">The recipient.</param>
    /// <param name="template">A catalog template name, already validated by the caller.</param>
    /// <param name="locale">The render locale.</param>
    /// <param name="toHash">The hex SHA-256 of the address with the pepper.</param>
    /// <param name="paramsJson">The allow-listed, token-free render values as JSON.</param>
    /// <param name="now">The queueing instant, from the caller's clock.</param>
    /// <returns>The new aggregate, already carrying its event.</returns>
    public static EmailMessage Queue(
        Guid userId,
        string template,
        Language locale,
        string toHash,
        string paramsJson,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(toHash);
        ArgumentNullException.ThrowIfNull(paramsJson);

        var message = new EmailMessage
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Template = template,
            Locale = locale,
            ToHash = toHash,
            ParamsJson = paramsJson,
            Status = EmailStatus.Queued,
            CreatedAt = now,
        };

        message.Raise(new EmailRequested(message.Id, userId, template, locale, paramsJson, now));

        return message;
    }

    /// <summary>Records the start of one attempt.</summary>
    /// <param name="now">
    /// The attempt instant. It is not persisted: 00 §6 carries no <c>last_attempt_at</c> column, and the
    /// attempt timeline lives in the Worker log and in <c>email_send_seconds</c> [INFERENCE 09.2.6-T3].
    /// </param>
    /// <exception cref="InvalidOperationException">The message was already sent; the job's guard is the only caller.</exception>
    public void Sending(DateTimeOffset now)
    {
        if (Status == EmailStatus.Sent)
        {
            throw new InvalidOperationException(
                $"E-mail '{Id}' was already sent at {SentAt:O}; a sent message is never attempted again (09 §5.3).");
        }

        Attempts++;
        LastErrorCode = null;
    }

    /// <summary>
    /// Records success. The render values are dropped here — the row is the audit trail of what was sent,
    /// not a second copy of it (09 README §B10 WT17).
    /// </summary>
    /// <param name="providerMessageId">The id the SMTP message carried.</param>
    /// <param name="now">The acceptance instant, from the job's clock.</param>
    public void Sent(string providerMessageId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerMessageId);

        Status = EmailStatus.Sent;
        ProviderMessageId = providerMessageId;
        LastErrorCode = null;
        ParamsJson = null;
        SentAt = now;
    }

    /// <summary>Records a failed attempt, keeping the render values so the next attempt can retry them.</summary>
    /// <param name="errorCode">The wire code of the failure (09 README D17), never a message.</param>
    /// <param name="now">
    /// The failure instant; like <see cref="Sending"/> it is not persisted (00 §6 has no <c>failed_at</c>)
    /// and the run's history is the Worker log [INFERENCE 09.2.6-T3].
    /// </param>
    public void Failed(string errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        Status = EmailStatus.Failed;
        LastErrorCode = errorCode;
    }
}

/// <summary>
/// A message was queued for delivery (09 README BD9, <c>email_requested.v1</c>): ids, the template name
/// and locale, and the allow-listed, token-free render values.
/// </summary>
/// <remarks>
/// It carries no address and no token: the address is resolved from <c>users.email</c> at send time and a
/// token-bearing value is minted there too (09 README §B10 WT17, WT30). The Worker's handler only
/// enqueues <c>SendEmailJob</c> with <see cref="EmailMessageId"/> (09 README D11/D12), so the queue row
/// stays free of content as well.
/// </remarks>
/// <param name="EmailMessageId">The <c>email_messages</c> row to send.</param>
/// <param name="UserId">The owner the job runs as.</param>
/// <param name="Template">The catalog template name.</param>
/// <param name="Locale">The render locale.</param>
/// <param name="ParamsJson">The render values as JSON; <c>null</c>-free, token-free.</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("email_requested.v1")]
public sealed record EmailRequested(
    Guid EmailMessageId,
    Guid UserId,
    string Template,
    Language Locale,
    string ParamsJson,
    DateTimeOffset OccurredAt) : IDomainEvent;
