using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Logout.Commands;

/// <summary>
/// The one handler of <c>POST /auth/logout</c> (09.3.2-T1 Design, WF12): it hands the presented cookie
/// value to <see cref="IAuthSessionService"/>, which revokes the whole family.
/// </summary>
/// <remarks>
/// WF12 makes logout revoke the <em>family</em>, not one row: every device that shares a family loses
/// the session together, and the access token that is already out dies at its own expiry (≤ 5 min for an
/// owner, ≤ 15 min for a user).
/// </remarks>
/// <param name="sessions">The credential-session flow.</param>
public sealed class LogoutHandler(IAuthSessionService sessions)
    : ICommandHandler<LogoutCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> Handle(LogoutCommand command, CancellationToken cancellationToken)
        => new(sessions.LogoutAsync(command, cancellationToken));
}
