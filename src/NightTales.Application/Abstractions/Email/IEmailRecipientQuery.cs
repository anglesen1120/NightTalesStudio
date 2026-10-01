using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The recipient of one message as the platform knows it right now (09 README BD9, 09.2.6-T3).
/// </summary>
/// <param name="Address">The current <c>users.email</c> value.</param>
/// <param name="Status">The account's lifecycle state; only <see cref="UserStatus.Active"/> may be mailed.</param>
public sealed record EmailRecipient(string Address, UserStatus Status);

/// <summary>
/// Resolves the address a message goes to (09 README BD9, 09.2.6-T3): the job reads it at send time, so
/// <c>email_messages</c> stores the address once — in <c>users</c> — and an address changed between queue
/// and send is followed instead of duplicated [DECISION].
/// </summary>
/// <remarks>
/// <para>
/// It is a read of the Identity table, which is why it is a port: the <c>users</c> row is infrastructure
/// (<c>AppUser</c>), and the Application layer must not see it. It is implemented over the read model, so
/// the Worker host — which registers no Identity manager — resolves it exactly as the Api does.
/// </para>
/// <para>
/// The lookup is by id under an explicit predicate: <c>users</c> is the tenant root and carries no tenant
/// filter (09 README D10), and the caller is either a handler acting for that same user or
/// <c>SendEmailJob</c> already pinned to it.
/// </para>
/// </remarks>
public interface IEmailRecipientQuery
{
    /// <summary>Reads the recipient of one message.</summary>
    /// <param name="userId">The owner the message row names.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The address and status, or <c>not_found</c> when no such account exists.</returns>
    Task<ErrorOr<EmailRecipient>> GetAsync(Guid userId, CancellationToken cancellationToken);
}
