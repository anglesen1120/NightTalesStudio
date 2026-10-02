using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Auth.Password.ResetPassword.Commands;

/// <summary>
/// The frozen <c>POST /api/v1/auth/reset-password</c> body (09.3-auth plan §5, 09 README BD3): the token
/// the SPA read from the link's URL fragment, and the new password.
/// </summary>
/// <remarks>
/// The token arrives in the body and never in the URL of this call, so it cannot reach an access log on
/// the way back in either. <see cref="AllowAnonymousRequestAttribute"/> is the D4 marker: the caller holds
/// only the mailed link.
/// </remarks>
/// <param name="Token">The presented single-use token; never logged.</param>
/// <param name="NewPassword">The requested password; never logged.</param>
[NotAudited("ResetPasswordHandler writes the account.password_reset row itself, with the caller's IP and User-Agent (09.3.3-T1).")]
[AllowAnonymousRequest]
public sealed record ResetPasswordCommand(string Token, string NewPassword) : ICommand<ErrorOr<Success>>;
