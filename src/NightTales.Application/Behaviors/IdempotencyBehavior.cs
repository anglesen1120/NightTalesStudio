using ErrorOr;
using Mediator;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Claims an <c>IIdempotentCommand</c>'s key inside the UnitOfWork transaction and replays the stored
/// success value without running the handler (09 README D6).
/// </summary>
/// <remarks>
/// Pass-through placeholder: 09.1.3-T2 fills the body before any feature command lands.
/// </remarks>
public sealed class IdempotencyBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
        => next(message, cancellationToken);
}
