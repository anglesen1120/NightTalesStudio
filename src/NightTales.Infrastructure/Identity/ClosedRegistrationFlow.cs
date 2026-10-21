using ErrorOr;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Auth.Register.Commands;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The P registration flow (09 README BD2, W2-O1/W17 and WF05): sign-ups are closed, so every request
/// answers 403 <c>registration_closed</c> and nothing is written.
/// </summary>
/// <remarks>
/// This is the complete "closed" behaviour, not a stub: 09.3.1-T3 replaces the DI registration with
/// <c>ModeAwareRegistrationFlow</c> and leaves the command, the endpoint, the validator and the handler
/// untouched. The flow is a singleton (it holds no state) and writes no row, so the handler's
/// transaction commits nothing.
/// </remarks>
public sealed class ClosedRegistrationFlow : IRegistrationFlow
{
    /// <summary>The stable description of the closed-mode refusal (no account data, no e-mail echo).</summary>
    public const string ClosedDescription = "registration_closed";

    /// <inheritdoc />
    public Task<ErrorOr<Success>> RegisterAsync(RegisterCommand command, RequestInfo info, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Task.FromResult<ErrorOr<Success>>(
            Error.Forbidden(ErrorCodes.RegistrationClosed, ClosedDescription));
    }
}
