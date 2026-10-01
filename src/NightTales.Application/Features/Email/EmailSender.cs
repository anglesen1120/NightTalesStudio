using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using DomainEmailMessage = NightTales.Domain.Communication.EmailMessage;

namespace NightTales.Application.Features.Email;

/// <summary>
/// Queues an e-mail: validates the template and its values, resolves the recipient's hash, writes the
/// <c>email_messages</c> row and lets the outbox interceptor write <c>email_requested.v1</c> in the same
/// transaction (09 README BD9/D11, 09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// It never sends: the message reaches an SMTP server only in the Worker's <c>SendEmailJob</c>, so a
/// request path never waits on a mail server and a delivery failure never rolls back the command that
/// asked for the mail (09 README D11/H4).
/// </para>
/// <para>
/// It also never stores an address or a token: the row keeps the SHA-256 of the address with the platform
/// pepper (09 README BD16) and the values the template's producer allow-list accepts. A key the template
/// does not accept — including a token-bearing one such as <c>link</c> — is refused with
/// <c>email_template_keys</c> before anything is written (09 README §B10 WT30).
/// </para>
/// <para>
/// All producers are command handlers or outbox handlers, so this always joins an existing unit of work
/// and never calls <c>SaveChangesAsync</c> itself (09 README D5).
/// </para>
/// </remarks>
/// <param name="database">The write model the row is added to.</param>
/// <param name="recipients">Resolves the current address of the owner.</param>
/// <param name="hasher">Turn the address into the stored hash.</param>
/// <param name="clock">The queueing instant.</param>
/// <param name="metrics">The queued counter of 09.2.6-T3.</param>
public sealed class EmailSender(
    IAppDbContext database,
    IEmailRecipientQuery recipients,
    IEmailAddressHasher hasher,
    IClock clock,
    EmailMetrics metrics) : IEmailSender
{
    /// <inheritdoc />
    public async Task<ErrorOr<Guid>> QueueAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var definition = EmailTemplateCatalog.Find(message.Template);
        if (definition is null)
        {
            return Error.Validation(
                code: ErrorCodes.EmailTemplateUnknown,
                description: $"'{message.Template}' is not a platform e-mail template.");
        }

        var unknownKey = FirstUnknownKey(message.Params, definition);
        if (unknownKey is not null)
        {
            return Error.Validation(
                code: ErrorCodes.EmailTemplateKeys,
                description: $"'{unknownKey}' is not a value the '{message.Template}' template accepts from its producer.");
        }

        var recipient = await recipients.GetAsync(message.UserId, cancellationToken).ConfigureAwait(false);
        if (recipient.IsError)
        {
            return recipient.Errors;
        }

        var row = DomainEmailMessage.Queue(
            message.UserId,
            message.Template,
            message.Locale,
            hasher.Hash(recipient.Value.Address),
            EmailParamsJson.ToJson(message.Params),
            clock.UtcNow);

        database.EmailMessages.Add(row);
        metrics.Queued(message.Template);

        return row.Id;
    }

    /// <summary>The first key the template's producer allow-list refuses, or <see langword="null"/>.</summary>
    /// <param name="values">The values the caller passed.</param>
    /// <param name="definition">The template's definition.</param>
    private static string? FirstUnknownKey(
        IReadOnlyDictionary<string, string> values,
        EmailTemplateDefinition definition)
    {
        foreach (var key in values.Keys)
        {
            if (!definition.ProducerKeys.Contains(key))
            {
                return key;
            }
        }

        return null;
    }
}
