using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Auth.Mfa.RecoveryCodes.Commands;

/// <summary>
/// The one handler of <c>POST /me/mfa/recovery-codes</c> (09.10.4-T1 Design): a thin delegator to
/// <see cref="IMfaEnrolmentService"/>, which replaces the set, audits the change and alerts.
/// </summary>
/// <param name="enrolment">The MFA lifecycle flow.</param>
/// <param name="currentUser">The caller whose codes are replaced.</param>
/// <param name="requestInfo">The caller metadata the audit row records.</param>
public sealed class RegenerateRecoveryCodesHandler(
    IMfaEnrolmentService enrolment,
    ICurrentUser currentUser,
    IRequestInfoAccessor requestInfo)
    : ICommandHandler<RegenerateRecoveryCodesCommand, ErrorOr<IReadOnlyList<string>>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<IReadOnlyList<string>>> Handle(
        RegenerateRecoveryCodesCommand command,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return ValueTask.FromResult<ErrorOr<IReadOnlyList<string>>>(MfaErrors.Unauthenticated());
        }

        return new(enrolment.RegenerateRecoveryCodesAsync(userId, requestInfo.Current, cancellationToken));
    }
}
