using ErrorOr;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// Verifies an e-mail/password pair against the Identity store (09 README BD2), returning the
/// <see cref="AppUser"/> on success and mapping every failure to 401 <c>invalid_credentials</c>.
/// </summary>
/// <remarks>
/// <para>
/// Declared here, not in <c>Application/Abstractions/Identity/</c> as 09.3.1-T1's design sketches:
/// <see cref="AppUser"/> is an Infrastructure type (09.2.1-T1) and the Application assembly may not
/// reference Infrastructure (09 README D1, enforced by <c>AssemblyReferenceRules</c>), so a port that
/// exposes it cannot compile there. Consumers (09.3.2-T1's login handler) need the same ruling.
/// </para>
/// <para>
/// The implementation is scoped: it depends on the scoped <c>UserManager&lt;AppUser&gt;</c>.
/// </para>
/// </remarks>
public interface ICredentialVerifier
{
    /// <summary>
    /// Returns the user when the password matches; otherwise 401 <c>invalid_credentials</c>.
    /// An unknown e-mail takes the same path and cost as a wrong password (BD4 timing rule).
    /// </summary>
    /// <param name="email">The submitted address; never logged.</param>
    /// <param name="password">The submitted password; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<AppUser>> VerifyAsync(string email, string password, CancellationToken ct);

    /// <summary>
    /// Re-authenticates an already-known user without mapping an error (09.10.4-T1's re-auth guard):
    /// returns <c>false</c> for a wrong password instead of an error result.
    /// </summary>
    /// <param name="user">The user to re-authenticate.</param>
    /// <param name="password">The submitted password; never logged.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<bool> VerifyPasswordAsync(AppUser user, string password, CancellationToken ct);
}
