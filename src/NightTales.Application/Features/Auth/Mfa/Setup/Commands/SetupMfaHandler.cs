using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Auth.Mfa.Setup.Commands;

/// <summary>
/// The one handler of <c>POST /me/mfa/setup</c> (09.10.4-T1 Design): it resolves the caller and hands the
/// work to <see cref="IMfaEnrolmentService"/>, which owns the secret, the audit row and the
/// <c>otpauth://</c> URI.
/// </summary>
/// <remarks>
/// Thin on purpose, exactly like 09.3.2-T1's login handler: the flow needs <c>AppUser</c>, which the
/// Application assembly cannot reference, so it lives in Infrastructure behind the port (see
/// <see cref="IMfaEnrolmentService"/>).
/// </remarks>
/// <param name="enrolment">The MFA lifecycle flow.</param>
/// <param name="currentUser">The caller whose account is enrolled.</param>
/// <param name="requestInfo">The caller metadata the audit row records.</param>
public sealed class SetupMfaHandler(
    IMfaEnrolmentService enrolment,
    ICurrentUser currentUser,
    IRequestInfoAccessor requestInfo)
    : ICommandHandler<SetupMfaCommand, ErrorOr<MfaSetup>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<MfaSetup>> Handle(
        SetupMfaCommand command,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return ValueTask.FromResult<ErrorOr<MfaSetup>>(MfaErrors.Unauthenticated());
        }

        return new(enrolment.StartSetupAsync(userId, requestInfo.Current, cancellationToken));
    }
}
