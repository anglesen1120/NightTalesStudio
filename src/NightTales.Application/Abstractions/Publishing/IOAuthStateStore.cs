using NightTales.Domain.Publishing;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The raw-SQL half of <c>oauth_states</c> (06.1.2-T1, 06 README D3): the single atomic claim that
/// authenticates the anonymous callback. The tracked insert, the purge and the domain shape stay in the
/// Application layer; only the statement that must be one <c>UPDATE … RETURNING</c> lives behind this port.
/// </summary>
/// <remarks>
/// <para>
/// The statement has to be raw for two reasons, exactly like 09.3.2-T1's rotation: the claim must be one
/// atomic <c>UPDATE</c> whose <c>WHERE</c> decides the winner, so two Api instances claiming the same
/// state can never both see a row; and the row is deliberately not <c>IUserOwned</c>, so its read cannot
/// go through the tenant filter of a scope that has no principal (06 README D3/F01, YR10).
/// </para>
/// <para>
/// <b>It runs inside the caller's transaction.</b> The implementation executes on the scope's
/// <c>AppDbContext</c> connection and must throw when no transaction is open, because the caller commits
/// the claim in unit of work #1 before it calls Google. The claim is never committed by this port itself.
/// </para>
/// </remarks>
public interface IOAuthStateStore
{
    /// <summary>
    /// Claims the state row of <paramref name="stateHash"/> for <paramref name="platform"/>:
    /// <c>UPDATE oauth_states SET used_at = @now WHERE state_hash = @hash AND platform = @platform AND
    /// used_at IS NULL AND expires_at &gt; @now RETURNING …</c>.
    /// </summary>
    /// <param name="stateHash">
    /// The lowercase SHA-256 hex of the presented state. An unknown, spent or expired state all match no
    /// row.
    /// </param>
    /// <param name="platform">
    /// The platform of the callback's route segment. A state minted for another platform matches no row, so
    /// it answers the same indistinguishable <see langword="null"/> as an unknown state (F8) — the check is
    /// defence in depth while only YouTube has a route (07 adds the second one).
    /// </param>
    /// <param name="now">The claim instant, from the scope's clock.</param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns>
    /// The claimed row's values, or <see langword="null"/> when the statement matched nothing. The caller
    /// answers every <see langword="null"/> with the one indistinguishable failure outcome, so this port
    /// never reports why.
    /// </returns>
    Task<OAuthStateClaim?> ClaimAsync(string stateHash, Platform platform, DateTimeOffset now, CancellationToken ct);
}
