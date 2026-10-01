using ErrorOr;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The one writer and reader of <c>refresh_tokens</c> (09.3.2-T1 Design, BD1/BD3): a family's first row,
/// the locking rotation, the family revocation and the durable re-authentication counters.
/// </summary>
/// <remarks>
/// <para>
/// Declared in Infrastructure for the same layer reason as <see cref="ITokenIssuer"/>: an
/// <see cref="AppUser"/> parameter cannot appear in an Application-side interface (09 README D1/D2).
/// </para>
/// <para>
/// Every method must be called inside the caller's UnitOfWork transaction (09 README D5): the rotation
/// takes <c>SELECT … FOR UPDATE</c> on the presented row with the raw connection, and the revocation is
/// a set-based <c>UPDATE</c> that has to commit with the command that asked for it, or not at all. The
/// whole rotation is one transaction, so there is never a partial family row.
/// </para>
/// </remarks>
public interface IRefreshTokenService
{
    /// <summary>Opens a new family for <paramref name="user"/> and returns its plaintext token once.</summary>
    /// <param name="user">The user who signed in; supplies the role whose session limits apply.</param>
    /// <param name="info">The caller metadata recorded on the row.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<IssuedRefreshSession>> StartSessionAsync(AppUser user, RequestInfo info, CancellationToken ct);

    /// <summary>
    /// Rotates <paramref name="presentedToken"/>: the successor inherits the family, the ceiling and the
    /// re-authentication counters, while the presented row is revoked as <c>rotated</c>.
    /// </summary>
    /// <remarks>
    /// The three failure answers are errors because they never need a committed row of their own: an
    /// unknown cookie and a stale revoked row write nothing, and a reuse detection revokes the family
    /// before it answers — the caller maps both to a success-typed outcome so that revocation commits
    /// ([RULED — README §B10 WT29]).
    /// </remarks>
    /// <param name="presentedToken">The token presented in the cookie; only its hash is looked up.</param>
    /// <param name="info">The caller metadata recorded on the successor row.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<RotatedSession>> RotateAsync(string presentedToken, RequestInfo info, CancellationToken ct);

    /// <summary>Revokes every live row of one family in one statement.</summary>
    /// <param name="familyId">The family to revoke.</param>
    /// <param name="reason">A <c>revoked_reason</c> wire value (see <c>RevocationReasons</c>).</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> RevokeFamilyAsync(Guid familyId, string reason, CancellationToken ct);

    /// <summary>Revokes every live family of one user, optionally sparing one family.</summary>
    /// <param name="userId">The user whose sessions are revoked.</param>
    /// <param name="reason">A <c>revoked_reason</c> wire value.</param>
    /// <param name="exceptFamilyId">A family to keep alive (the caller's own session), or null.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>The number of rows revoked.</returns>
    Task<ErrorOr<int>> RevokeAllFamiliesAsync(Guid userId, string reason, Guid? exceptFamilyId, CancellationToken ct);

    /// <summary>Records a successful re-authentication on the family's live row (BD4).</summary>
    /// <param name="familyId">The family that re-authenticated.</param>
    /// <param name="now">The instant of the successful re-authentication.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<Success>> MarkReauthenticatedAsync(Guid familyId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Charges one wrong re-authentication attempt to the family and returns the new durable count
    /// (BD4, [RULED — README §B10 WT32]).
    /// </summary>
    /// <remarks>
    /// The increment clamps at 5 (<c>LEAST(reauth_attempts + 1, 5)</c>): two concurrent wrong attempts
    /// right after the fifth must not violate the column's CHECK and surface as a 500 instead of a 401.
    /// </remarks>
    /// <param name="familyId">The family the attempt belongs to.</param>
    /// <param name="now">The instant of the failed attempt.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<int>> RegisterReauthFailureAsync(Guid familyId, DateTimeOffset now, CancellationToken ct);

    /// <summary>The family's durable re-authentication state (BD4; consumed by 09.10.4-T1).</summary>
    /// <param name="familyId">The family to read.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<ReauthState>> GetReauthStateAsync(Guid familyId, CancellationToken ct);
}
