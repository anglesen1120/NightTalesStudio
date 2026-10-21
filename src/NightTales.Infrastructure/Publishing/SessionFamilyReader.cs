using ErrorOr;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Security;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The publishing side's view of a session family's re-authentication state (06.1.2-T1): the connect and callback
/// flows ask whether the family a state row points at is still alive, and this forwards that read to the session
/// store that owns the row.
/// </summary>
/// <remarks>
/// The port exists because <c>refresh_tokens</c> and <see cref="IRefreshTokenService"/> belong to 09 and only the
/// Infrastructure half may name them: a handler in Application states the one question it needs, and the answer is
/// the same <see cref="ReauthState"/> the re-auth guard reads. The callback consumes <c>Revoked</c> only, so a
/// family whose row is gone — or whose presented state predates its last failure — is refused without this port
/// ever explaining why.
/// </remarks>
/// <param name="refreshTokens">The session store of 09.3.2-T1, read unchanged.</param>
public sealed class SessionFamilyReader(IRefreshTokenService refreshTokens) : ISessionFamilyReader
{
    /// <inheritdoc />
    public Task<ErrorOr<ReauthState>> GetReauthStateAsync(Guid familyId, CancellationToken ct) =>
        refreshTokens.GetReauthStateAsync(familyId, ct);
}
