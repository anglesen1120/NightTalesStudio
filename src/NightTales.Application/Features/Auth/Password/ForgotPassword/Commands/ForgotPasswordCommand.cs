using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Auth.Password.ForgotPassword.Commands;

/// <summary>
/// The frozen <c>POST /api/v1/auth/forgot-password</c> body (09.3-auth plan §5, 09 README BD16): the
/// submitted address and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The answer never depends on the body: an unknown address, an inactive account and a live one all
/// answer the identical 202 (no enumeration), which is why the handler's own outcome is always success
/// and the difference is only operational (a queued mail, an audit row, a counter).
/// </para>
/// <para>
/// <see cref="AllowAnonymousRequestAttribute"/> is the D4 marker: the route is anonymous by definition,
/// and without it the default-deny authorization behaviour would answer 401 before the handler ran.
/// </para>
/// </remarks>
/// <param name="Email">The submitted address; never logged and never echoed.</param>
[NotAudited("ForgotPasswordHandler writes the account.password_reset_requested row itself, and only for an account that exists and is active (09.3.3-T1).")]
[AllowAnonymousRequest]
public sealed record ForgotPasswordCommand(string Email) : ICommand<ErrorOr<Success>>;
