using ErrorOr;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Auth.Login.Commands;
using NightTales.Application.Features.Auth.Logout.Commands;
using NightTales.Application.Features.Auth.Refresh.Commands;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// What the three credential routes of 09.3.2-T1 do, behind one seam: password login, refresh-token
/// rotation and logout (09 README BD1/BD3).
/// </summary>
/// <remarks>
/// <para>
/// This port exists because of the layer rule, not by taste (09 README D1/D2). The login flow must read
/// and write <c>AppUser</c>, open a refresh family and sign with the ES256 key; the first two are
/// Infrastructure types (<c>AppUser</c> belongs to <c>NightTales.Infrastructure.Persistence</c>), and the
/// Application assembly may not reference Infrastructure — enforced by
/// <c>AssemblyReferenceRules.Application_ReferencesOnlyAllowedPackages</c>. So the flow's work lives in
/// Infrastructure (<c>RefreshSessionService</c>) and the Application handlers stay thin delegators,
/// exactly like 09.3.1-T1's <c>RegistrationGateHandler</c> over <c>IRegistrationFlow</c>.
/// </para>
/// <para>
/// Every outcome the caller must be able to see is <em>success-typed</em> where the command wrote
/// something that must commit: a reuse detection or an expired family revokes rows and then answers 401,
/// so the handler returns <c>LoginOutcome</c>/<c>RefreshOutcome</c> rather than an error and the endpoint
/// maps it ([RULED — README §B10 WT29]). Only an outcome that must leave the database untouched —
/// <c>email_not_verified</c> — is returned as an error, which rolls the surrounding transaction back.
/// </para>
/// <para>
/// It runs inside the handler's UnitOfWork transaction (09 README D5), so the rotation, the revocation
/// and the audit row that later tasks attach to them commit or roll back together.
/// </para>
/// </remarks>
public interface IAuthSessionService
{
    /// <summary>Attempts one password login.</summary>
    /// <param name="command">The validated request body.</param>
    /// <param name="info">The caller metadata recorded with the new family.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<LoginResult>> LoginAsync(LoginCommand command, RequestInfo info, CancellationToken ct);

    /// <summary>Rotates the presented refresh token and issues a fresh access token.</summary>
    /// <param name="command">The presented refresh token and the caller metadata.</param>
    /// <param name="info">The caller metadata recorded on the successor row.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<RefreshResult>> RefreshAsync(RefreshCommand command, RequestInfo info, CancellationToken ct);

    /// <summary>Revokes the family the presented refresh token belongs to (WF12).</summary>
    /// <param name="command">The presented refresh token.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> LogoutAsync(LogoutCommand command, CancellationToken ct);
}
