using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using DomainEmailMessage = NightTales.Domain.Communication.EmailMessage;

namespace NightTales.Application.Features.Email;

/// <summary>
/// Reads the delivery state of one message through the read model (09 README D8/D10, 09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// One projection query, no tracked entity: the caller wants six columns of one row, and the tenant filter
/// is already on the context (the row is <c>IUserOwned</c>), so another user's id is
/// <c>not_found</c> — the same answer as an unknown id — without a predicate written by hand.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model.</param>
public sealed class EmailDeliveryStatusQuery(IReadDb readDb) : IEmailDeliveryStatus
{
    /// <inheritdoc />
    public async Task<ErrorOr<EmailDelivery>> GetAsync(Guid emailMessageId, CancellationToken cancellationToken)
    {
        var delivery = await readDb
            .Query<DomainEmailMessage>()
            .Where(message => message.Id == emailMessageId)
            .Select(message => new EmailDelivery(
                message.Id,
                message.Status,
                message.Attempts,
                message.LastErrorCode,
                message.SentAt,
                message.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return delivery is null ? Errors.NotFound() : delivery;
    }
}
