using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Login.Mfa.Commands;

/// <summary>
/// The one handler of <c>POST /auth/login/mfa</c> (09.3.2-T3 Design): a thin delegator to
/// <see cref="IMfaChallengeService"/>, which owns the challenge's lock, the attempt counter, the code
/// verification, the audit rows and the token issuing.
/// </summary>
/// <remarks>
/// The handler resolves the caller's metadata and hands the three submitted values plus the pre-auth
/// cookie over; it deliberately decides nothing, so the whole attempt stays inside one transaction
/// (09 README D5). Keeping it thin is what lets the flow be the single owner of the order the Design
/// fixes.
/// </remarks>
/// <param name="challenges">The MFA challenge flow.</param>
/// <param name="requestInfo">The caller metadata the audit rows and the new family record.</param>
public sealed class LoginMfaHandler(IMfaChallengeService challenges, IRequestInfoAccessor requestInfo)
    : ICommandHandler<LoginMfaCommand, ErrorOr<MfaChallengeOutcome>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<MfaChallengeOutcome>> Handle(
        LoginMfaCommand command,
        CancellationToken cancellationToken) =>
        new(challenges.CompleteAsync(
            new MfaChallengeRequest(
                command.MfaToken,
                command.Code,
                command.RecoveryCode,
                command.PreAuthCookie),
            requestInfo.Current,
            cancellationToken));
}
