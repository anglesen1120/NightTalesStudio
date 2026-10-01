using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Realtime.Commands.IssueHubTicket;

/// <summary>
/// The one handler of <c>POST /api/v1/hubs/ticket</c> (09.8.1-T2 Design): it mints the ticket, hands
/// only its hash to the store and returns the plaintext with the expiry the row was given.
/// </summary>
/// <remarks>
/// <para>
/// The ticket is <see cref="HubTicketSecret"/>'s — 256 random bits as base64url (BD16) — and the
/// handler never logs it: the value is a bearer secret, and the one place it may appear is the HTTP
/// response body.
/// </para>
/// <para>
/// The row is written inside the D5 transaction the pipeline opened, so the insert and the store's
/// opportunistic delete of this user's expired rows commit together.
/// </para>
/// </remarks>
/// <param name="store">The <c>hub_tickets</c> writer.</param>
/// <param name="currentUser">The authenticated caller the ticket is issued to (D10).</param>
public sealed class IssueHubTicketHandler(IHubTicketStore store, ICurrentUser currentUser)
    : ICommandHandler<IssueHubTicketCommand, ErrorOr<HubTicketDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<HubTicketDto>> Handle(
        IssueHubTicketCommand command,
        CancellationToken cancellationToken)
    {
        // `RequireAuthorization` on the route already refuses an anonymous caller; this is the
        // defence in depth for a scope that reaches the handler without a subject, and it never
        // mints a ticket for nobody.
        if (currentUser.Id is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Authentication is required.");
        }

        var ticket = HubTicketSecret.NewTicket();
        var expiresAt = await store
            .IssueAsync(userId, HubTicketSecret.HashOf(ticket), cancellationToken)
            .ConfigureAwait(false);

        return new HubTicketDto(ticket, expiresAt);
    }
}
