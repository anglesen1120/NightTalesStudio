using ErrorOr;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The one way a feature queues an e-mail (09 README BD9/D11, 09.2.6-T3): it writes the
/// <c>email_messages</c> row and, through the outbox interceptor, the <c>email_requested.v1</c> message of
/// the caller's own transaction.
/// </summary>
/// <remarks>
/// <para>
/// It <b>queues only</b>: nothing in a request path talks to an SMTP server, so a slow or unreachable mail
/// server can never slow down or fail the command that asked for the message. Delivery is the Worker's
/// <c>SendEmailJob</c>, on the <c>email</c> queue with five retries (09 §5.3).
/// </para>
/// <para>
/// A caller that has to know the outcome polls <see cref="IEmailDeliveryStatus"/> (06.2.2-T3's veto notice
/// gates a publication consent on a delivered e-mail, 02.8.2/02.8.3 alert their owner).
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>
    /// Queues <paramref name="message"/> for delivery.
    /// </summary>
    /// <param name="message">The typed message; see <see cref="EmailMessage"/> for what it may carry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// The <c>email_messages.id</c>, or an error: <c>email_template_unknown</c> for a name outside the
    /// catalog, <c>email_template_keys</c> for a value the template does not accept, <c>not_found</c> when
    /// the recipient does not exist.
    /// </returns>
    Task<ErrorOr<Guid>> QueueAsync(EmailMessage message, CancellationToken cancellationToken);
}
