using ErrorOr;
using Mediator;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Denies by default and honours <c>IRequireRole</c> / <c>[AllowAnonymousRequest]</c>.
/// </summary>
/// <remarks>
/// Pass-through placeholder: 09.1.3-T2 fills the body (default-deny, role check) before any feature
/// command lands; the slot and its position in <see cref="MediatorPipeline.Behaviors"/> are final.
/// </remarks>
public sealed class AuthorizationBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
        => next(message, cancellationToken);
}
