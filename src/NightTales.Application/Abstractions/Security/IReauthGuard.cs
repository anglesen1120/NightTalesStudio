using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The recent-re-authentication gate of the sensitive routes (09 README BD3/BD4): the endpoint filter
/// <c>RequireReauth(window)</c> is its thin caller, and later plans attach that filter to their own
/// routes (settings, role change, MFA reset, grants).
/// </summary>
/// <remarks>
/// <para>
/// The guard reads the live family, never the token: the caller's <c>sid</c> claim names the
/// <c>refresh_tokens</c> family whose <c>reauthenticated_at</c> is compared with the window. A token that
/// still looks valid therefore cannot satisfy the gate once its family was revoked.
/// </para>
/// <para>
/// A caller with no family (an anonymous request, a system scope, or the enrolment-only token of BD4)
/// has nothing to re-authenticate: it answers 401 <c>reauth_required</c>.
/// </para>
/// </remarks>
public interface IReauthGuard
{
    /// <summary>
    /// Requires a successful re-authentication of the caller's family no longer ago than
    /// <paramref name="window"/>.
    /// </summary>
    /// <param name="window">How recent the re-authentication must be (BD4: 5 minutes).</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// Success, 401 <c>reauth_required</c> when the family has no recent re-authentication (or none at
    /// all), or 401 <c>session_revoked</c> when the family is already dead.
    /// </returns>
    Task<ErrorOr<Success>> RequireRecentAsync(TimeSpan window, CancellationToken ct);
}
