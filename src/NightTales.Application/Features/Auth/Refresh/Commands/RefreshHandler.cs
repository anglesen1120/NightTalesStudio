using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Features.Auth.Refresh.Commands;

/// <summary>
/// The one handler of <c>POST /auth/refresh</c> (09.3.2-T1): it hands the presented cookie value and the
/// ambient caller metadata to <see cref="IAuthSessionService"/>, which rotates the family and re-checks
/// the account.
/// </summary>
/// <remarks>
/// The <c>Origin</c> check is not here: it is the endpoint filter (<c>RequireSameOriginFilter</c>), so a
/// cross-site request is refused before a handler, a transaction or a rotation exists (BD3/BD16).
/// </remarks>
/// <param name="sessions">The credential-session flow.</param>
/// <param name="requestInfo">The caller metadata of the scope (IP/User-Agent).</param>
public sealed class RefreshHandler(IAuthSessionService sessions, IRequestInfoAccessor requestInfo)
    : ICommandHandler<RefreshCommand, ErrorOr<RefreshResult>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<RefreshResult>> Handle(RefreshCommand command, CancellationToken cancellationToken)
        => new(sessions.RefreshAsync(command, requestInfo.Current, cancellationToken));
}
