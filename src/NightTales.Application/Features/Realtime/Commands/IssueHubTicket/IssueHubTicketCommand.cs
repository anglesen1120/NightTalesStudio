using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Realtime.Commands.IssueHubTicket;

/// <summary>
/// <c>POST /api/v1/hubs/ticket</c> (09 README BD11, 09.8.1-T2 Design): exchange the caller's access
/// token for one short-lived, single-use hub ticket.
/// </summary>
/// <remarks>
/// No body and no idempotency key: the ticket lives 60 seconds and is single use, so a retry is
/// cheaper as a new ticket than as a replay of this one (09.8.1-T2 Design). The command carries no
/// user id either — the handler reads the caller from the scope (D10), exactly like every other
/// command.
/// </remarks>
[NotAudited(
    "A ticket is one short-lived row for one connection attempt; an audit row per connect is noise "
    + "09 L214 does not ask for (09.8.1-T2 Notes).")]
public sealed record IssueHubTicketCommand : ICommand<ErrorOr<HubTicketDto>>;

/// <summary>
/// What the client connects with: the plaintext ticket and its expiry (09 README BD11's
/// <c>{ ticket, expiresAt }</c>).
/// </summary>
/// <remarks>
/// The plaintext exists only in this response — the table stores the hash (BD16), and the ticket is
/// never logged.
/// </remarks>
/// <param name="Ticket">The 43-character base64url ticket.</param>
/// <param name="ExpiresAt">When the ticket stops being claimable: <c>created_at + Realtime:TicketTtlSeconds</c>.</param>
public sealed record HubTicketDto(string Ticket, DateTimeOffset ExpiresAt);
