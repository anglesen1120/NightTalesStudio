using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Mfa.Reauth.Commands;

/// <summary>
/// The one handler of <c>POST /auth/reauth</c> (09.10.4-T1 Design): a thin delegator to
/// <see cref="IReauthService"/>, which owns the password check, the TOTP check, the durable counter, the
/// family revocation and the audit row.
/// </summary>
/// <remarks>
/// The handler resolves the caller's metadata and passes the two submitted values through; it deliberately
/// does not decide anything, so the whole attempt stays inside one transaction (09 README D5).
/// </remarks>
/// <param name="reauth">The re-authentication flow.</param>
/// <param name="requestInfo">The caller metadata the audit row records.</param>
public sealed class ReauthHandler(IReauthService reauth, IRequestInfoAccessor requestInfo)
    : ICommandHandler<ReauthCommand, ErrorOr<ReauthResult>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<ReauthResult>> Handle(
        ReauthCommand command,
        CancellationToken cancellationToken) =>
        new(reauth.ReauthenticateAsync(
            command.Password,
            command.Code,
            requestInfo.Current,
            cancellationToken));
}
