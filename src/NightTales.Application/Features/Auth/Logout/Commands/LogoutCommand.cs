using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Auth.Logout.Commands;

/// <summary>
/// The <c>POST /api/v1/auth/logout</c> request (09.3.2-T1 Design): the refresh token the Api read from the
/// <c>__Secure-nts_rt</c> cookie.
/// </summary>
/// <remarks>
/// Unlike login and refresh this is <em>not</em> an anonymous route: logout runs for an authenticated
/// caller ([RULED — README §B10 WT12] logout revokes the family, and the family is resolved from the
/// presented token). The route carries the <c>Origin</c> filter as well, because it is a
/// cookie-authenticated POST (BD3/BD16).
/// </remarks>
/// <param name="PresentedToken">The presented cookie value; never logged.</param>
public sealed record LogoutCommand(string PresentedToken) : ICommand<ErrorOr<Success>>;
