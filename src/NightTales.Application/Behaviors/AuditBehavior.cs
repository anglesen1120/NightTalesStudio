using System.Text.Json;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Audit;
using NightTales.Domain.Common;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Writes the <c>audit_logs</c> row of an <c>[Audited]</c> command — pipeline slot 6, the innermost
/// behaviour, so the row joins the transaction <c>UnitOfWorkBehavior</c> opened and commits with the
/// change (09 README D4/D5, BD15).
/// </summary>
/// <remarks>
/// <para>
/// Slot order is the whole contract (plan §3 consequences): the write happens inside the open
/// transaction, so a rolled-back command leaves no row; an error response writes nothing; and an
/// idempotent replay short-circuits in slot 5, <em>outside</em> Audit, so a replay never duplicates a
/// row. The behaviour therefore runs the handler first and writes only when the response is a success.
/// </para>
/// <para>
/// The row's <c>data</c> comes from <see cref="IAuditData.ToAuditData"/> alone — never from the request
/// body — and is serialized with no naming policy, so the projection's keys reach <c>jsonb</c> verbatim
/// (they are the frozen per-action contract of 09.3.6-T2). A projection that names no subject means a
/// self-service command: the caller is the account the row is about, which is what makes a user-visible
/// row satisfy the table's <c>ck_audit_logs_user_visible_has_subject</c> constraint.
/// </para>
/// <para>
/// <see cref="AuditEntry.Ip"/> and <see cref="AuditEntry.UserAgent"/> are always <c>null</c> here: they
/// belong to the security rows the auth handlers write themselves through <see cref="IAuditWriter"/>
/// (plan §8: ordinary commands leave both null). For the same reason this behaviour takes no request
/// metadata port — the Api's <c>IRequestInfoAccessor</c> is the one place a caller's address is read
/// (09.3.1-T1), and it is not used on this path.
/// </para>
/// <para>
/// A message with neither <c>[Audited]</c> nor an <see cref="IAuditData"/> projection passes through
/// untouched: queries skip slot 6 entirely (D4) and a command that argued its exemption with
/// <c>[NotAudited]</c> writes nothing here.
/// </para>
/// </remarks>
/// <param name="writer">The one writer of <c>audit_logs</c>; it adds to the ambient transaction.</param>
/// <param name="currentUser">The actor of the scope, used for the actor id and the actor type.</param>
/// <param name="metadata">The per-request-type attribute lookup (a singleton cache).</param>
/// <param name="clock">The scope's clock; the row's instant is read once, after the handler ran.</param>
public sealed class AuditBehavior<TMessage, TResponse>(
    IAuditWriter writer,
    ICurrentUser currentUser,
    AuditMetadataCache metadata,
    IClock clock)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    /// <summary>
    /// The projection serializer: compact, and deliberately without a naming policy so the summary's keys
    /// are stored exactly as the command declared them.
    /// </summary>
    private static readonly JsonSerializerOptions DataSerializer = new() { WriteIndented = false };

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(message, cancellationToken).ConfigureAwait(false);

        if (!response.IsError
            && message is IAuditData projection
            && metadata.TryGet<TMessage>(out var attribute))
        {
            await writer.WriteAsync(Build(attribute, projection), cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>Projects one <c>AuditEntry</c> from the attribute, the command's projection and the actor.</summary>
    private AuditEntry Build(AuditedAttribute attribute, IAuditData projection)
    {
        var data = projection.ToAuditData();
        var subjectUserId = data.SubjectUserId ?? currentUser.Id;

        return new AuditEntry(
            ActorUserId: currentUser.Id,
            ActorType: ActorTypeOf(subjectUserId),
            SubjectUserId: subjectUserId,
            Action: attribute.Action,
            Category: attribute.Category,
            Visibility: attribute.Visibility,
            EntityType: data.EntityType,
            EntityId: data.EntityId,
            DataJson: JsonSerializer.Serialize(data.Summary, DataSerializer),
            Ip: null,
            UserAgent: null,
            ActorLabel: null,
            OccurredAt: clock.UtcNow);
    }

    /// <summary>
    /// Derives <c>actor_type</c> (plan §3): <see cref="ActorType.System"/> when the scope itself is the
    /// system actor — a Worker job or an outbox handler runs as one user through
    /// <c>ISystemContext.For</c>, and such a row must not claim that user wrote it — otherwise
    /// <see cref="ActorType.User"/> when the caller is the subject and
    /// <see cref="ActorType.Admin"/> when a staff caller acts on another account.
    /// </summary>
    /// <param name="subjectUserId">The account the row is about.</param>
    private ActorType ActorTypeOf(Guid? subjectUserId)
    {
        if (currentUser.Actor == ActorType.System)
        {
            return ActorType.System;
        }

        if (subjectUserId is { } subject && currentUser.Id == subject)
        {
            return ActorType.User;
        }

        return currentUser.Actor == ActorType.Admin ? ActorType.Admin : ActorType.System;
    }
}
