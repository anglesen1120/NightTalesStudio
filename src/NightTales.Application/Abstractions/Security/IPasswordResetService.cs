using ErrorOr;
using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// What the two password routes of 09.3.3-T1 do, behind one seam: request a reset link and spend one
/// (09 README BD3/BD16, WF11/WF12).
/// </summary>
/// <remarks>
/// <para>
/// This port exists because of the layer rule, not by taste (09 README D1/D2). The flows must read and
/// write the Identity user (<c>AppUser</c>), change its password through <c>UserManager</c>, rotate the
/// security stamp and revoke the refresh families — all Infrastructure types — while the command, the
/// validator, the endpoint and the handler stay in Application, exactly as 09.3.2-T1's login flow does
/// behind <c>IAuthSessionService</c>.
/// </para>
/// <para>
/// Both methods run inside the handler's <c>UnitOfWork</c> transaction (09 README D5): the audit row, the
/// token consume, the password change and the queued e-mail commit or roll back together. The
/// implementation never calls <c>SaveChangesAsync</c> itself.
/// </para>
/// <para>
/// A request never reveals whether the account exists (BD16): <see cref="RequestResetAsync"/> answers
/// success for an unknown or inactive address exactly as for a live one, queues nothing and writes no row
/// that would differentiate. It is the Api endpoint that pads both paths to the same timing floor.
/// </para>
/// </remarks>
public interface IPasswordResetService
{
    /// <summary>
    /// Queues the <c>password_reset</c> message for the account behind <paramref name="email"/>, when
    /// there is one and it is active.
    /// </summary>
    /// <param name="email">The submitted address; validated for shape by the handler, never logged.</param>
    /// <param name="info">The caller metadata; the audit row records it, the e-mail never does.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>
    /// Always success — including for an unknown or inactive address and for a queue failure, both of
    /// which are swallowed for the response (BD16/WT30) and surfaced only operationally.
    /// </returns>
    Task<ErrorOr<Success>> RequestResetAsync(string email, RequestInfo info, CancellationToken cancellationToken);

    /// <summary>
    /// Spends one reset token and gives the account a new password, revoking everything the old one
    /// unlocked.
    /// </summary>
    /// <param name="token">The presented plaintext token; never logged.</param>
    /// <param name="newPassword">The new password, already accepted by <c>IPasswordPolicy</c>.</param>
    /// <param name="info">The caller metadata; the audit row records it.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>
    /// Success, or 400 <c>validation_failed</c> for a used, expired, wrong-kind or unknown token, an
    /// account that is no longer active, and any Identity refusal — the four are deliberately one answer.
    /// </returns>
    Task<ErrorOr<Success>> ResetAsync(string token, string newPassword, RequestInfo info, CancellationToken cancellationToken);
}
