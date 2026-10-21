using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Publishing.Commands.CancelPublicationByLink;

/// <summary>
/// <c>POST /api/v1/publications/cancel-by-link { publicationId, token }</c> (06.2.2-T2 §Design "Cancel-by-link",
/// 06 README D7/F17/YR8): the anonymous half of the veto window, which cancels exactly one
/// <c>awaiting_consent</c> publication from the single-use link in the veto-notice e-mail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is anonymous, and why that is not a hole.</b> <see cref="AllowAnonymousRequestAttribute"/> is the
/// D4 marker: the caller holds only the mailed link, which may be opened in another browser or after the
/// session ended. The token <em>is</em> the authentication — 32 random bytes, stored only as a hash, single
/// use, and scoped to one publication — and the route sits behind the <c>auth</c> rate policy (BD14) so the
/// space cannot be probed.
/// </para>
/// <para>
/// <b>One refusal for every failure.</b> A used, expired or unknown token, a token minted for another
/// publication, an unknown or foreign publication and a row that has left <c>awaiting_consent</c> all answer
/// the same <c>400 cancel_token_invalid</c> body (09 §4's no-state-leak rule; the task's acceptance check
/// compares the four byte for byte). Only a transient store failure keeps its own answer, because a 503 says
/// nothing about the token.
/// </para>
/// <para>
/// <b>No <c>If-Match</c> and no <c>Idempotency-Key</c>.</b> The token is the single-use guard and the row's own
/// state is the precondition, so a repeat is refused by the spent token rather than answered as a success: the
/// link is a capability for one cancellation, not a retryable request (06.2.2 plan §7).
/// </para>
/// <para>
/// <b>It writes its audit row itself.</b> <see cref="NotAuditedAttribute"/> is deliberate:
/// <c>publishing.cancelled</c> is the action <c>CancelPublicationCommand</c>'s <c>[Audited]</c> projection
/// already owns, and one action belongs to exactly one command (the architecture rule
/// <c>Audited_ActionsAreUnique</c>); this route adds the <c>system:link</c> actor to that same frozen row, so
/// the handler writes it through <c>IAuditWriter</c> inside the same transaction, exactly as the anonymous
/// connect callback does for <c>publishing.channel_connected</c>.
/// </para>
/// </remarks>
/// <param name="PublicationId">The publication the link names; compared with the token's own subject.</param>
/// <param name="Token">
/// The plaintext token from the link's URL fragment. It is never logged, audited or echoed in the response.
/// </param>
[AllowAnonymousRequest]
[NotAudited(
    "publishing.cancelled is written by this handler with actor system:link; the action and its frozen keys "
    + "belong to CancelPublicationCommand's [Audited] projection (one action, one owner).")]
public sealed record CancelPublicationByLinkCommand(Guid PublicationId, string Token)
    : ICommand<ErrorOr<Success>>;
