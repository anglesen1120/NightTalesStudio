using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// Reads the recipient of one message from the Identity table (09 README BD9, 09.2.6-T3).
/// </summary>
/// <remarks>
/// <para>
/// It goes through the read model, so the Worker — which registers no Identity manager — resolves it
/// exactly as the Api does. The lookup is one projection by primary key: <c>users</c> is the tenant root
/// and carries no tenant filter (09 README D10), and both callers already act for that same user.
/// </para>
/// <para>
/// An unknown id is <c>not_found</c>; the job turns that into <c>email_user_gone</c> without a retry, and
/// the queueing side treats it as an error the caller may map to its own response.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model.</param>
internal sealed class EmailRecipientQuery(IReadDb readDb) : IEmailRecipientQuery
{
    /// <inheritdoc />
    public async Task<ErrorOr<EmailRecipient>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var recipient = await readDb
            .Query<AppUser>()
            .Where(user => user.Id == userId)
            .Select(user => new EmailRecipient(user.Email ?? string.Empty, user.Status))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return recipient is null ? Errors.NotFound() : recipient;
    }
}
