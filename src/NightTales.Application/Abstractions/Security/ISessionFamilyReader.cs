using ErrorOr;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The read side of one refresh family for callers the Application layer owns (06.1.2-T1's callback asks
/// whether the family that started a connect still has a live row).
/// </summary>
/// <remarks>
/// <para>
/// The port exists because of the layer rule, not by taste: the member that owns the read is 09.3.2-T1's
/// <c>IRefreshTokenService.GetReauthStateAsync</c>, which cannot be declared in this assembly because its
/// neighbours take <c>AppUser</c> (09 README D1/D2). No second session mechanism and no raw SQL against
/// <c>refresh_tokens</c> is introduced: an implementation forwards to that member, unchanged, and the
/// callback consumes only <see cref="ReauthState.Revoked"/>.
/// </para>
/// <para>
/// It runs inside the caller's transaction, like every other reader of that table (09 README D5).
/// </para>
/// </remarks>
public interface ISessionFamilyReader
{
    /// <summary>The family's durable re-authentication state (09.3.2-T1 BD4).</summary>
    /// <param name="familyId">The family to read.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The state, or an error when the read fails. A family with no live row answers
    /// <see cref="ReauthState.Revoked"/> = <see langword="true"/>, which is how the callback refuses a
    /// connect started from a session that has since died.
    /// </returns>
    Task<ErrorOr<ReauthState>> GetReauthStateAsync(Guid familyId, CancellationToken ct);
}
