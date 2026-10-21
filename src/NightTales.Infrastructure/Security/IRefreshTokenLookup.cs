using ErrorOr;

namespace NightTales.Infrastructure.Security;

/// <summary>The owner of one presented refresh token, read from <c>refresh_tokens</c> by its unique hash.</summary>
/// <param name="UserId">The <c>user_id</c> column, the account the flow re-checks before it issues.</param>
/// <param name="FamilyId">The <c>family_id</c> column, the family logout revokes.</param>
public sealed record RefreshTokenOwner(Guid UserId, Guid FamilyId);

/// <summary>
/// The read-only half of <c>refresh_tokens</c> that the credential flows need but
/// <see cref="IRefreshTokenService"/> deliberately does not expose: resolve a presented token to the
/// account and the family it belongs to (09.3.2-T1 Design's "one indexed read" of
/// <c>refresh_tokens.user_id</c>, and logout's family lookup).
/// </summary>
/// <remarks>
/// <para>
/// A separate port rather than two more members on <see cref="IRefreshTokenService"/>: that interface's
/// member list is signed (09.3.2-T2 and 09.10.4-T1 reuse it by name), and this lookup is a plain indexed
/// read that needs no transaction — unlike every write on the other port.
/// </para>
/// <para>
/// Implemented by the same <c>EfRefreshTokenService</c>, so one type still owns the table.
/// </para>
/// </remarks>
public interface IRefreshTokenLookup
{
    /// <summary>Resolves <paramref name="presentedToken"/> to its owner.</summary>
    /// <remarks>
    /// <para>
    /// The lookup ignores <c>revoked_at</c> on purpose: a refresh rotation consumes the presented row and
    /// then re-checks the account, so the flow must still find the row it just rotated.
    /// </para>
    /// <para>
    /// No row is <c>401 session_revoked</c> — the answer a client with a dead cookie must get (WT21) — while
    /// a database that cannot answer is <c>503 service_unavailable</c>. Those two must never be the same
    /// value: a plain <c>null</c> made an outage look like an invalid session, which would sign a working
    /// client out (09.3.2-T1 CR-2).
    /// </para>
    /// </remarks>
    /// <param name="presentedToken">The token presented in the cookie; only its hash is looked up.</param>
    /// <param name="ct">The request cancellation token.</param>
    Task<ErrorOr<RefreshTokenOwner>> FindAsync(string presentedToken, CancellationToken ct);
}
