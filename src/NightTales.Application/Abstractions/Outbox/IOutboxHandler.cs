using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Outbox;

/// <summary>
/// One consumer of one domain event delivered by the Worker's outbox dispatcher (09 README D11,
/// 09.2.4 plan §3). Handlers are registered with
/// <see cref="OutboxServiceCollectionExtensions.AddOutboxHandler{TEvent, THandler}"/> and run in
/// registration order, each in its own scope under <c>ISystemContext.For(user_id)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is <b>at least once</b>: a Worker that dies after a handler ran but before the row was marked
/// processed delivers the message again, and a handler that overruns its lease can be delivered twice. A
/// handler is therefore idempotent by contract and carries its own idempotency key (02 D-something:
/// <c>bible:{episodeId}:{storyVersionId}</c>).
/// </para>
/// <para>
/// A handler that changes data sends a command through <c>ISender</c>, so the pipeline and the UnitOfWork
/// apply; a handler that only enqueues work calls
/// <see cref="NightTales.Application.Abstractions.Jobs.IJobScheduler"/>. It never writes the
/// <c>outbox_messages</c> row itself — the dispatcher owns every state transition of that row.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">
/// The event the handler consumes. It is contravariant, so a handler written for a base event type may be
/// registered for a derived one.
/// </typeparam>
public interface IOutboxHandler<in TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Handles <paramref name="domainEvent"/> once.</summary>
    /// <param name="domainEvent">The event, deserialized from the stored payload.</param>
    /// <param name="context">The delivery: the row id, its user, the attempt and the occurrence.</param>
    /// <param name="cancellationToken">Cancels the delivery (the Worker's shutdown token).</param>
    ValueTask HandleAsync(TEvent domainEvent, OutboxContext context, CancellationToken cancellationToken);
}
