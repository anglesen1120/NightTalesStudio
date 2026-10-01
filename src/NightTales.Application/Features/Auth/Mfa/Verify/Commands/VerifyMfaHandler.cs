using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Auth.Mfa.Verify.Commands;

/// <summary>
/// The one handler of <c>POST /me/mfa/verify</c> (09.10.4-T1 Design): a thin delegator to
/// <see cref="IMfaEnrolmentService"/>, which verifies the pending secret, promotes it, turns
/// <c>two_factor_enabled</c> on, rotates the stamp and issues the recovery codes.
/// </summary>
/// <param name="enrolment">The MFA lifecycle flow.</param>
/// <param name="currentUser">The caller confirming the enrolment.</param>
/// <param name="requestInfo">The caller metadata the audit row records.</param>
public sealed class VerifyMfaHandler(
    IMfaEnrolmentService enrolment,
    ICurrentUser currentUser,
    IRequestInfoAccessor requestInfo)
    : ICommandHandler<VerifyMfaCommand, ErrorOr<IReadOnlyList<string>>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<IReadOnlyList<string>>> Handle(
        VerifyMfaCommand command,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return ValueTask.FromResult<ErrorOr<IReadOnlyList<string>>>(MfaErrors.Unauthenticated());
        }

        return new(enrolment.VerifyAsync(userId, command.Code, requestInfo.Current, cancellationToken));
    }
}
