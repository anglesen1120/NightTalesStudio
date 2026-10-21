using ErrorOr;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// Reads the delivery state of one queued e-mail (09 README BD9, 09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// The read goes through the tenant filter (09 README D10): the <c>email_messages</c> row is
/// <c>IUserOwned</c>, so another user's message is <c>not_found</c> — the same 404 as an unknown id, with
/// no predicate written by hand and nothing to leak by probing.
/// </para>
/// <para>
/// It is a poll, not a callback: the promise is only that the state is readable after each attempt
/// commits, so a caller that waits on it owns its own timing and give-up policy (06.2.2-T3 [OPEN]).
/// </para>
/// </remarks>
public interface IEmailDeliveryStatus
{
    /// <summary>Reads the state of one message of the caller's own tenant.</summary>
    /// <param name="emailMessageId">The id <see cref="IEmailSender.QueueAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The delivery, or <c>not_found</c> when no such message exists in this tenant.</returns>
    Task<ErrorOr<EmailDelivery>> GetAsync(Guid emailMessageId, CancellationToken cancellationToken);
}
