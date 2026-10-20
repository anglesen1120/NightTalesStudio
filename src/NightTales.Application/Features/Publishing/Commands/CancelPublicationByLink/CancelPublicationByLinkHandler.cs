using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Commands.CancelPublication;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.Application.Features.Publishing.Commands.CancelPublicationByLink;

/// <summary>
/// Cancels one <c>awaiting_consent</c> publication from the single-use cancel link of the veto-notice e-mail
/// (06.2.2-T2 §Design "Cancel-by-link", 06 README D7/F17/YR8): spends the token, checks that the token was minted
/// for the publication the body names, locks and reads the owner's row, moves it to <c>cancelled</c>, gives back
/// any quota unit it still holds, records <c>publishing.cancelled</c> with the actor <c>system:link</c> and lets
/// the outbox interceptor raise <c>publication_updated.v1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the refusal contract.</b> The token is spent <em>first</em>, in one statement, so the four
/// failures the task's acceptance check compares (spent / expired / unknown token, another publication's token,
/// unknown or foreign publication, a row that left <c>awaiting_consent</c>) all cost one cheap refusal and
/// answer the identical <c>400 cancel_token_invalid</c> body. Nothing before the consume can distinguish a
/// caller who guessed a publication id from one who holds a genuine link, and the cheap checks after it never
/// reveal which of them failed.
/// </para>
/// <para>
/// <b>A refusal spends nothing.</b> The consume runs on this scope's connection inside the transaction
/// <c>UnitOfWorkBehavior</c> opened, and that behaviour rolls the transaction back on every error response
/// (<c>UC3</c>: an error leaves nothing behind). So a subject mismatch, a missing or foreign row, a row that left
/// <c>awaiting_consent</c> or a failed quota release leaves <c>used_at</c> <see langword="null"/>: only a
/// <em>committed</em> cancellation spends the token. That is the invariant worth keeping — a refused request
/// changes nothing at all, so a legitimate owner whose client mis-sent the id can present the same link again —
/// and the single statement still serialises two concurrent callers, because both block on the row lock and the
/// loser's <c>UPDATE … WHERE used_at IS NULL</c> re-reads the committed winner.
/// </para>
/// <para>
/// <b>Identity without a principal.</b> The route is anonymous, so the scope is not tenant-pinned (the unit of
/// work builds the context before this handler runs). The consume's user id is therefore the identity the rest
/// of the command runs under: the locked read takes it as its own <c>user_id</c> predicate
/// (<see cref="IPublicationCancelLock"/>), so another owner's row and an unknown id are both <c>null</c>, and
/// the audit row's subject is that same owner. The id the body names is never trusted for tenancy — only to be
/// compared with the token's subject.
/// </para>
/// <para>
/// <b>The failure that is not a failure of the link.</b> A transient token-store failure keeps its own
/// <c>503 service_unavailable</c> (09 README D5): it reveals nothing about whether the token exists, and it is
/// the answer a client should retry. Everything the route owes indistinguishability to is the 400.
/// </para>
/// <para>
/// <b>Ids only.</b> The audit <c>data</c> repeats <c>CancelPublicationCommand</c>'s four frozen keys (publication,
/// episode, the status the row left, the actor) with <c>actor = system:link</c>; the log lines carry the same ids
/// and an internal reason label. The token, the owner's address, the title and the channel display name are
/// never written to either (06 README D17).
/// </para>
/// </remarks>
/// <param name="tokens">The single-use store of 09 README BD3; the consume is the route's only authentication.</param>
/// <param name="publications">
/// The one locked read of the anonymous path, which carries the token owner's tenancy itself.
/// </param>
/// <param name="quotaLedger">The platform quota ledger the row's reserved unit is given back to (D10).</param>
/// <param name="audit">The same writer the pipeline uses, inside the same transaction.</param>
/// <param name="clock">The scope's clock; one instant is read for the transition and the audit row.</param>
/// <param name="logger">The log of the route; every line carries ids only (D17).</param>
public sealed partial class CancelPublicationByLinkHandler(
    IUserActionTokenService tokens,
    IPublicationCancelLock publications,
    IQuotaLedger quotaLedger,
    IAuditWriter audit,
    IClock clock,
    ILogger<CancelPublicationByLinkHandler> logger)
    : ICommandHandler<CancelPublicationByLinkCommand, ErrorOr<Success>>
{
    /// <summary>The action the audit row records — the same one the authenticated cancel owns.</summary>
    private const string AuditAction = "publishing.cancelled";

    /// <summary>
    /// The longest value that can be a minted token: the minter issues 43 base64url characters, so anything past
    /// this bound is not one and is refused without a store call.
    /// </summary>
    private const int MaxTokenLength = 128;

    /// <summary>
    /// The refusal the route answers for <em>every</em> failed attempt: one code, one description, no metadata —
    /// so the four acceptance bodies are the same bytes (09 §4's no-state-leak rule).
    /// </summary>
    private static readonly Error InvalidLink = Error.Validation(
        code: ErrorCodes.CancelTokenInvalid,
        description: "The cancel link is invalid, already used or expired.");

    /// <summary>The audit projection serializer of the pipeline, matched so the stored keys are verbatim.</summary>
    private static readonly JsonSerializerOptions DataSerializer = new() { WriteIndented = false };

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        CancelPublicationByLinkCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: spend the token. This single statement is the route's authentication and its replay guard; an
        // absent value is the same refusal as an unknown one (the erroring consume of 09 §4), and only the
        // transient store failure keeps its own answer (09 README D5). A value that cannot be a minted token
        // (empty, or longer than any token the minter issues: 43 base64url characters) is refused before the
        // hash and the statement, so a bogus body costs no store round trip (06.2.2-T2 perf review PERF-1).
        if (string.IsNullOrEmpty(command.Token) || command.Token.Length > MaxTokenLength)
        {
            return Refuse("token", command.PublicationId);
        }

        var consumed = await tokens
            .ConsumeAsync(command.Token, UserActionKind.PublicationCancel, cancellationToken)
            .ConfigureAwait(false);

        if (consumed.IsError)
        {
            if (string.Equals(consumed.FirstError.Code, ErrorCodes.ServiceUnavailable, StringComparison.Ordinal))
            {
                LogStoreUnavailable(consumed.FirstError.Code);

                return consumed.Errors;
            }

            return Refuse("token", command.PublicationId);
        }

        // Step 2: the token must have been minted for the publication this body names (YR8). The subject is the
        // row's, returned by the consume, never a value the caller supplied. A mismatch answers the same generic
        // refusal as everything else, and the unit of work's rollback leaves the token unspent (see the remarks).
        if (consumed.Value.SubjectId != command.PublicationId)
        {
            return Refuse("subject", command.PublicationId);
        }

        // Step 3: the locked read under the token owner's identity. `FOR NO KEY UPDATE` is the lock the
        // authenticated cancel and the upload job take, so whoever wins decides; `null` is another owner's row or
        // an unknown id, and both are the same refusal.
        var publication = await publications
            .LockOwnedAsync(consumed.Value.UserId, command.PublicationId, cancellationToken)
            .ConfigureAwait(false);

        if (publication is null)
        {
            return Refuse("missing", command.PublicationId);
        }

        // Step 4: only a row still inside the veto window can be cancelled from the link. A repeat from the link
        // itself never reaches here — the committed cancellation spent the token — so this guard covers the other
        // route to a non-awaiting row (the owner cancelled or consented in the app, or the row left via another
        // flow), and it answers the generic refusal rather than the sibling route's idempotent success.
        if (publication.Status != PublicationStatus.AwaitingConsent)
        {
            return Refuse("status", publication.Id);
        }

        var now = clock.UtcNow;

        // Step 5: the shared cancel step (06.2.6-T1, reused by 06.4.1-T1's disconnect) reads the status the row
        // leaves and asks the aggregate for the transition; this route names itself `system:link` (STATUS S72-4).
        // The guard above makes the aggregate's accepting arm the only one reachable, and a refusal here is a
        // concurrent change, which is the same generic answer.
        var cancelled = PublicationCancelStep.Cancel(publication, now, CancelAuditKeys.ActorLink);

        if (cancelled.IsError)
        {
            return Refuse("state", publication.Id);
        }

        var outcome = cancelled.Value;

        // Step 6: the quota unit the row still holds, if any, through the one shared release: an autopilot row in
        // `awaiting_consent` normally holds none, but the invariant is the helper's, not this route's. A ledger
        // refusal fails the command, so a committed cancel never strands a reservation.
        var released = await PublicationQuotaRelease
            .ReleaseAsync(publication, quotaLedger, logger, cancellationToken)
            .ConfigureAwait(false);

        if (released.IsError)
        {
            return released.Errors;
        }

        // Step 7: the audit row, written here because slot 6's projection belongs to the sibling command (one
        // action, one [Audited] owner). It joins the transaction the unit of work opened, so it commits with the
        // state change and its outbox row (09 README D5).
        await audit
            .WriteAsync(
                new AuditEntry(
                    ActorUserId: null,
                    ActorType: ActorType.System,
                    SubjectUserId: publication.UserId,
                    Action: AuditAction,
                    Category: Category.Publishing,
                    Visibility: Visibility.User,
                    EntityType: CancelAuditKeys.EntityType,
                    EntityId: publication.Id,
                    DataJson: JsonSerializer.Serialize(Summary(outcome), DataSerializer),
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: null,
                    OccurredAt: now),
                cancellationToken)
            .ConfigureAwait(false);

        LogCancelled(outcome.PublicationId, outcome.EpisodeId, outcome.PreviousStatus);

        return Result.Success;
    }

    /// <summary>
    /// The audit <c>data</c> of a link cancellation: the ids, the status the row left and the actor label — the
    /// four frozen keys of the sibling command, with this route's actor value.
    /// </summary>
    /// <param name="outcome">What the shared cancel step reported for the row this request cancelled.</param>
    private static Dictionary<string, object> Summary(PublicationCancelledOutcome outcome) =>
        new(StringComparer.Ordinal)
        {
            [CancelAuditKeys.PublicationIdKey] = outcome.PublicationId.ToString("D", CultureInfo.InvariantCulture),
            [CancelAuditKeys.EpisodeIdKey] = outcome.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
            [CancelAuditKeys.PreviousStatusKey] = outcome.PreviousStatus,
            [CancelAuditKeys.ActorKey] = outcome.Actor,
        };

    /// <summary>
    /// Answers the route's single refusal and records which check refused it — an internal label, never the token
    /// and never anything the owner typed.
    /// </summary>
    /// <param name="reason">The check that refused the attempt.</param>
    /// <param name="publicationId">The id the request named, for correlation.</param>
    private ErrorOr<Success> Refuse(string reason, Guid publicationId)
    {
        LogRefused(reason, publicationId);

        return InvalidLink;
    }

    [LoggerMessage(
        EventId = 6513,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} of episode {EpisodeId} was cancelled from {PreviousStatus} by its e-mail cancel link; no upload is sent.")]
    private partial void LogCancelled(Guid publicationId, Guid episodeId, string previousStatus);

    [LoggerMessage(
        EventId = 6514,
        Level = LogLevel.Information,
        Message = "A cancel link for publication {PublicationId} was refused by {Reason}; nothing was written.")]
    private partial void LogRefused(string reason, Guid publicationId);

    [LoggerMessage(
        EventId = 6515,
        Level = LogLevel.Warning,
        Message = "A cancel link could not be spent because the token store answered {ErrorCode}; nothing was written.")]
    private partial void LogStoreUnavailable(string errorCode);
}
