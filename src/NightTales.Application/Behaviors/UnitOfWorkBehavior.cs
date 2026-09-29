using ErrorOr;
using Mediator;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Opens an explicit <c>ReadCommitted</c> transaction before the handler, saves and commits on
/// success, rolls back on error, and maps <c>DbUpdateConcurrencyException</c> to
/// <c>concurrency_conflict</c>.
/// </summary>
/// <remarks>
/// Pass-through placeholder: 09.1.3-T2 fills the body (D5) before any feature command lands.
/// </remarks>
public sealed class UnitOfWorkBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
        => next(message, cancellationToken);
}
