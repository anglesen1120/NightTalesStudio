using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Password.ResetPassword.Commands;

/// <summary>
/// The one handler of <c>POST /auth/reset-password</c> (09 README BD2/BD3, WF12): it runs the password
/// policy first, then hands the token and the accepted password to <see cref="IPasswordResetService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract. <see cref="IPasswordPolicy.ValidateAsync"/> runs <em>before</em> the flow
/// touches anything, so a breached or too-short password is a 400 that leaves the token unspent — the user
/// can retry with a good one instead of needing a second mail. Only then does the flow consume the token,
/// change the password, rotate the stamp and revoke every refresh family in one transaction.
/// </para>
/// <para>
/// Thin on purpose, exactly like 09.3.2-T1's login handler: <c>AppUser</c>, <c>UserManager</c> and the
/// refresh-token store are Infrastructure types, so the port is the layer seam.
/// </para>
/// </remarks>
/// <param name="passwordPolicy">The BD2 pre-check of 09.3.1-T1 (length and breach).</param>
/// <param name="resets">The reset flow of 09.3.3-T1.</param>
/// <param name="requestInfo">The caller metadata of the scope (IP/User-Agent).</param>
public sealed class ResetPasswordHandler(
    IPasswordPolicy passwordPolicy,
    IPasswordResetService resets,
    IRequestInfoAccessor requestInfo) : ICommandHandler<ResetPasswordCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        ResetPasswordCommand command,
        CancellationToken cancellationToken)
    {
        var policy = await passwordPolicy
            .ValidateAsync(command.NewPassword, cancellationToken)
            .ConfigureAwait(false);

        if (policy.IsError)
        {
            return policy.Errors;
        }

        return await resets
            .ResetAsync(command.Token, command.NewPassword, requestInfo.Current, cancellationToken)
            .ConfigureAwait(false);
    }
}
