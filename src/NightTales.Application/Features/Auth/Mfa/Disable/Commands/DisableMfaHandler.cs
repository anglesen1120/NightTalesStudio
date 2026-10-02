using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Auth.Mfa.Disable.Commands;

/// <summary>
/// The one handler of <c>DELETE /me/mfa</c> (09.10.4-T1 Design): it refuses an owner or an admin with 403
/// <c>forbidden</c> and delegates everyone else to <see cref="IMfaEnrolmentService"/>.
/// </summary>
/// <remarks>
/// The role check is here rather than in the flow because it is a policy about the caller's claims, which
/// the Application side owns (BD4: an admin or owner cannot disable MFA — the role itself requires it).
/// 403 is the documented answer for this one case, unlike the 404 the platform answers for another user's
/// resource.
/// </remarks>
/// <param name="enrolment">The MFA lifecycle flow.</param>
/// <param name="currentUser">The caller's role and session.</param>
/// <param name="requestInfo">The caller metadata the audit row records.</param>
public sealed class DisableMfaHandler(
    IMfaEnrolmentService enrolment,
    ICurrentUser currentUser,
    IRequestInfoAccessor requestInfo)
    : ICommandHandler<DisableMfaCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> Handle(
        DisableMfaCommand command,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return ValueTask.FromResult<ErrorOr<Success>>(MfaErrors.Unauthenticated());
        }

        if (currentUser.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            // BD4: the two roles whose powers depend on a second factor cannot remove it.
            return ValueTask.FromResult<ErrorOr<Success>>(
                Error.Forbidden(
                    ErrorCodes.Forbidden,
                    "An owner or admin account cannot disable multi-factor authentication."));
        }

        return new(enrolment.DisableAsync(userId, requestInfo.Current, cancellationToken));
    }
}
