using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Login.Commands;

/// <summary>
/// The one handler of <c>POST /auth/login</c> (09.3.2-T1): it hands the validated command and the
/// ambient caller metadata to <see cref="IAuthSessionService"/>, which owns the whole credential and
/// session flow.
/// </summary>
/// <remarks>
/// The order the flow must keep — validator, credential verifier, status, confirmed e-mail, role/MFA —
/// is <c>RefreshSessionService</c>'s, in Infrastructure, because it needs <c>AppUser</c> and the Identity
/// store (see <see cref="IAuthSessionService"/> for the layer ruling). Keeping the handler thin is what
/// lets 09.3.2-T3 add the MFA challenge inside that flow without touching this type.
/// </remarks>
/// <param name="sessions">The credential-session flow.</param>
/// <param name="requestInfo">The caller metadata of the scope (IP/User-Agent).</param>
public sealed class LoginHandler(IAuthSessionService sessions, IRequestInfoAccessor requestInfo)
    : ICommandHandler<LoginCommand, ErrorOr<LoginResult>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<LoginResult>> Handle(LoginCommand command, CancellationToken cancellationToken)
        => new(sessions.LoginAsync(command, requestInfo.Current, cancellationToken));
}
