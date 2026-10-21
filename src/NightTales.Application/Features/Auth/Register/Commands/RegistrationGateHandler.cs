using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;

namespace NightTales.Application.Features.Auth.Register.Commands;

/// <summary>
/// The one handler of <c>POST /auth/register</c> (09 README BD2, WF05): it resolves the registered
/// <see cref="IRegistrationFlow"/> and hands it the validated command plus the ambient caller
/// metadata. In P that flow is <c>ClosedRegistrationFlow</c> and the answer is 403
/// <c>registration_closed</c> for every request; 09.3.1-T3 swaps the flow, not this handler.
/// </summary>
/// <remarks>
/// The handler is deliberately thin: the mode rules, the account work and the consent rows are the
/// flow's, so the P behaviour is the complete "closed" behaviour rather than a stub that T3 must
/// unpick. It runs inside the D4 pipeline, so the validator rejects shape errors before it and
/// <c>UnitOfWorkBehavior</c> commits or rolls back whatever the flow wrote.
/// </remarks>
/// <param name="flow">The registered registration flow (config-selected; P registers the closed one).</param>
/// <param name="requestInfo">The caller metadata of the scope (IP/User-Agent).</param>
public sealed class RegistrationGateHandler(IRegistrationFlow flow, IRequestInfoAccessor requestInfo)
    : ICommandHandler<RegisterCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        RegisterCommand command,
        CancellationToken cancellationToken)
        => await flow.RegisterAsync(command, requestInfo.Current, cancellationToken).ConfigureAwait(false);
}
