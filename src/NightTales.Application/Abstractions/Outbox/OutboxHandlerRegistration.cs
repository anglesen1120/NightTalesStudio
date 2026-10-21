using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Outbox;

/// <summary>
/// The non-generic face of one handler slot (09 README D11): what the Worker's dispatcher invokes without
/// knowing the event type at compile time.
/// </summary>
/// <remarks>
/// Feature code never implements this interface — it implements
/// <see cref="IOutboxHandler{TEvent}"/> and the registration wraps it. It exists so the dispatcher needs
/// no reflection and constructs exactly the handlers of the event it is delivering.
/// </remarks>
public interface IOutboxHandlerInvoker
{
    /// <summary>Handles <paramref name="domainEvent"/>, which is of the slot's event type.</summary>
    /// <param name="domainEvent">The deserialized event; the caller passes the type it was stored as.</param>
    /// <param name="context">The delivery.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    ValueTask HandleAsync(object domainEvent, OutboxContext context, CancellationToken cancellationToken);
}

/// <summary>
/// One <see cref="AddOutboxHandler{TEvent, THandler}"/> call (09 README D11): the event it consumes and a
/// factory that builds its handler in the scope of one delivery.
/// </summary>
/// <remarks>
/// <para>
/// The descriptors are registered as singletons and are enumerated in registration order, which is the
/// order the dispatcher calls the handlers of one message in. Enumerating them constructs nothing: the
/// handler is only built for the event type being delivered.
/// </para>
/// <para>
/// The factory is the closure the registration helper compiles for its two type arguments, so no
/// reflection and no open generic type is involved at dispatch time.
/// </para>
/// </remarks>
public sealed class OutboxHandlerRegistration
{
    private readonly Func<IServiceProvider, IOutboxHandlerInvoker> _create;

    internal OutboxHandlerRegistration(Type eventType, Func<IServiceProvider, IOutboxHandlerInvoker> create)
    {
        EventType = eventType;
        _create = create;
    }

    /// <summary>The event type this slot consumes.</summary>
    public Type EventType { get; }

    /// <summary>Builds the slot's handler in <paramref name="scope"/>.</summary>
    /// <param name="scope">The scope of one delivery, already pinned to the message's user.</param>
    public IOutboxHandlerInvoker Create(IServiceProvider scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return _create(scope);
    }
}

/// <summary>Builds one <see cref="IOutboxHandler{TEvent}"/> from a delivery's scope.</summary>
/// <typeparam name="TEvent">The event the handler consumes.</typeparam>
/// <typeparam name="THandler">The handler class registered for it.</typeparam>
internal sealed class OutboxHandlerInvoker<TEvent, THandler>(THandler handler) : IOutboxHandlerInvoker
    where TEvent : IDomainEvent
    where THandler : class, IOutboxHandler<TEvent>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(object domainEvent, OutboxContext context, CancellationToken cancellationToken) =>
        handler.HandleAsync((TEvent)domainEvent, context, cancellationToken);
}
