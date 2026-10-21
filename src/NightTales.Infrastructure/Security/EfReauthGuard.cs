using ErrorOr;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The live-family re-authentication guard of 09 README BD3/BD4: it reads
/// <c>refresh_tokens.reauthenticated_at</c> of the caller's <c>sid</c> family, never the token's claims.
/// </summary>
/// <remarks>
/// <para>
/// The difference matters: an access token stays valid for its whole lifetime, so a family that was
/// revoked (a logout on another device, a reuse detection, a password reset) must not satisfy a
/// sensitive route just because the caller still holds a signed token. Reading the family row is the
/// only answer that is right across two Api instances.
/// </para>
/// <para>
/// A caller with no family — an anonymous request, a system scope, or the enrolment-only token of BD4 —
/// has nothing to re-authenticate and is answered 401 <c>reauth_required</c>.
/// </para>
/// </remarks>
/// <param name="currentUser">The caller's claims; only the <c>sid</c> is read.</param>
/// <param name="refreshTokens">The family store, whose read is the guard's one statement.</param>
/// <param name="clock">The scope's clock reading, compared with the window.</param>
public sealed class EfReauthGuard(
    ICurrentUser currentUser,
    IRefreshTokenService refreshTokens,
    IClock clock) : IReauthGuard
{
    /// <inheritdoc />
    public async Task<ErrorOr<Success>> RequireRecentAsync(TimeSpan window, CancellationToken ct)
    {
        if (currentUser.SessionId is not { } familyId)
        {
            return MfaErrors.ReauthRequired();
        }

        var state = await refreshTokens.GetReauthStateAsync(familyId, ct).ConfigureAwait(false);
        if (state.IsError)
        {
            return state.Errors;
        }

        if (state.Value.Revoked)
        {
            // The family is gone: the session itself is invalid, which is a different (and stronger)
            // answer than "re-authenticate first".
            return Error.Unauthorized(
                ErrorCodes.SessionRevoked,
                "The session is no longer valid; sign in again.");
        }

        if (state.Value.ReauthenticatedAt is not { } reauthenticatedAt
            || reauthenticatedAt <= clock.UtcNow - window)
        {
            return MfaErrors.ReauthRequired();
        }

        return Result.Success;
    }
}
