using ErrorOr;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Auth.Register.Commands;

namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// What <c>POST /auth/register</c> does, behind one seam (09 README BD2, WF05): P registers
/// <c>ClosedRegistrationFlow</c> (403 <c>registration_closed</c> for every request) and 09.3.1-T3
/// replaces that registration with <c>ModeAwareRegistrationFlow</c> — the command, the endpoint, the
/// validator and the handler of 09.3.1-T1 stay exactly as they are.
/// </summary>
/// <remarks>
/// The implementation runs inside the handler's <c>UnitOfWork</c> transaction (09 README D5), so its
/// consent rows, its user row and its outbox rows commit or roll back together. It never calls
/// <c>SaveChangesAsync</c>.
/// </remarks>
public interface IRegistrationFlow
{
    /// <summary>Attempts one registration.</summary>
    /// <param name="command">The validated request body.</param>
    /// <param name="info">The caller metadata the flow records (consent IP).</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> RegisterAsync(RegisterCommand command, RequestInfo info, CancellationToken ct);
}
