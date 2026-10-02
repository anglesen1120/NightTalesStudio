using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Password.ForgotPassword.Commands;

/// <summary>
/// The one handler of <c>POST /auth/forgot-password</c> (09 README BD16/WT17/WT30): it hands the
/// validated address and the ambient caller metadata to <see cref="IPasswordResetService"/>, which owns
/// the lookup, the queued message and the audit row.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose, exactly like 09.3.2-T1's login handler: the flow needs <c>AppUser</c> and
/// <c>UserManager</c>, which the Application layer must not see, so the port is the layer seam and the
/// handler stays the request's contract.
/// </para>
/// <para>
/// The caller metadata is ambient (<see cref="IRequestInfoAccessor"/>), never part of the body: a client
/// must not be able to submit its own IP or User-Agent into the audit trail.
/// </para>
/// </remarks>
/// <param name="resets">The reset flow of 09.3.3-T1.</param>
/// <param name="requestInfo">The caller metadata of the scope (IP/User-Agent).</param>
public sealed class ForgotPasswordHandler(IPasswordResetService resets, IRequestInfoAccessor requestInfo)
    : ICommandHandler<ForgotPasswordCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        ForgotPasswordCommand command,
        CancellationToken cancellationToken) =>
        await resets
            .RequestResetAsync(command.Email, requestInfo.Current, cancellationToken)
            .ConfigureAwait(false);
}
