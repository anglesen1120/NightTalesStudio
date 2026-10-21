using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// Turns the domain events of the tracked aggregates into <c>outbox_messages</c> rows inside the same
/// <c>SaveChanges</c> as their aggregate, then clears them (09 README D11, 09.2.4 plan §3 step 3).
/// </summary>
/// <remarks>
/// <para>
/// It is registered on <see cref="AppDbContext"/> <em>after</em> <c>AuditableSaveChangesInterceptor</c>
/// (09 W3 CR-38): the auditable one stamps <c>created_at</c>/<c>updated_at</c> first, and the rows added
/// here are part of the same batch and the same command transaction, so a rolled-back command leaves no
/// event behind (D5).
/// </para>
/// <para>
/// <c>user_id</c> comes from the aggregate: only an <see cref="IUserOwned"/> aggregate has one, and an
/// event without a user must be marked <see cref="PlatformEventAttribute"/> — anything else is a bug in
/// the raising command, which fails here instead of dead-lettering later in the Worker (09 plan §8 UC6,
/// D10: a handler must never run unfiltered).
/// </para>
/// <para>
/// The events are cleared only after every row of this call was added: a <c>SaveChanges</c> that throws
/// keeps the aggregates untouched, so the caller can see the failure (and, for a transient failure, retry
/// the command it maps to).
/// </para>
/// </remarks>
/// <param name="registry">Resolves the stable <c>type</c> name of each event.</param>
public sealed class OutboxSaveChangesInterceptor(OutboxEventRegistry registry) : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Collect(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Adds one row per pending domain event and clears the aggregates that raised them.
    /// </summary>
    /// <param name="context">The context being saved; <c>null</c> when the event carries none.</param>
    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // Read first, add second: the tracker is enumerated while the rows are added to it.
        var raised = new List<(AggregateRoot Aggregate, IDomainEvent DomainEvent)>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is AggregateRoot aggregate && aggregate.DomainEvents.Count > 0)
            {
                raised.AddRange(aggregate.DomainEvents.Select(domainEvent => (aggregate, domainEvent)));
            }
        }

        if (raised.Count == 0)
        {
            return;
        }

        var traceParent = Activity.Current?.Id;

        foreach (var (aggregate, domainEvent) in raised)
        {
            context.Add(ToRow(aggregate, domainEvent, traceParent));
        }

        foreach (var aggregate in raised.Select(item => item.Aggregate).Distinct())
        {
            aggregate.ClearDomainEvents();
        }
    }

    /// <summary>One <c>outbox_messages</c> row for one raised event (09 README D11).</summary>
    private OutboxMessage ToRow(AggregateRoot aggregate, IDomainEvent domainEvent, string? traceParent)
    {
        var eventType = domainEvent.GetType();
        var userId = (aggregate as IUserOwned)?.UserId;

        if (userId is null && !OutboxEventRegistry.IsPlatformEvent(eventType))
        {
            throw new InvalidOperationException(
                $"The aggregate {aggregate.GetType().Name} is not IUserOwned, so the event "
                + $"{eventType.Name} has no user to run as; mark it [PlatformEvent] if it really is "
                + "platform-wide (09 README D10/D11).");
        }

        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = registry.NameOf(eventType),
            Payload = Serialize(domainEvent, eventType),
            UserId = userId,
            OccurredAt = domainEvent.OccurredAt,
            // Due immediately: the first delivery attempt is the first poll after the commit.
            NextAttemptAt = domainEvent.OccurredAt,
            TraceParent = traceParent,
        };
    }

    /// <summary>The event as the payload JSON of its own concrete type (09 plan §5).</summary>
    private static string Serialize(IDomainEvent domainEvent, Type eventType)
    {
        using var document = JsonSerializer.SerializeToDocument(domainEvent, eventType, OutboxJson.Options);

        return document.RootElement.GetRawText();
    }
}
