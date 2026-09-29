using ErrorOr;
using Mediator;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Writes the <c>audit_logs</c> row for an <c>[Audited]</c> command inside the UnitOfWork transaction,
/// so a rolled-back command leaves no audit row (09 README D4/D5).
/// </summary>
/// <remarks>
/// Reserved slot, pass-through until 09.3.6-T1 implements it (wave 2).
/// </remarks>
public sealed class AuditBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    public ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
        => next(message, cancellationToken);
}
