using System.Globalization;
using System.Text;
using ErrorOr;

namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The live access token of one channel connection (06.1.2-T2 task §"Token access", 06 README D9).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="AccessToken"/> is a bearer secret of the connected Google account: it is handed to the
/// caller in memory, never logged and never serialised (06 README D17).
/// </para>
/// <para>
/// <see cref="ExpiresAt"/> is the instant the token stops working, carried so a caller that opens a long
/// stream can decide whether to ask again before it does.
/// </para>
/// </remarks>
/// <param name="AccessToken">The decrypted bearer token for the YouTube Data API calls; never empty, never logged.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops being valid.</param>
public sealed record ChannelAccessToken(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Prints the expiry only (R8/F4): the compiler-generated list would put <see cref="AccessToken"/> into
    /// every log line, exception message and debugger string.
    /// </summary>
    /// <param name="builder">The record's string builder.</param>
    /// <returns><see langword="true"/>, because members were printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append(CultureInfo.InvariantCulture, $"ExpiresAt = {ExpiresAt}");
        return true;
    }
}

/// <summary>
/// The current access token of a channel connection, refreshed under the row lock when it is about to expire
/// (06.1.2-T2 task §"Token access", 06 README D9; consumed by 06.2.3-T2's uploads and 06.4.2-T1's refresh
/// sweep).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, one locking protocol.</b> The token pair lives in <c>channel_connections</c> under
/// <c>ITokenProtector</c> purpose <c>tokens.v1/{connectionId}</c> (06 README D4), so the refresh has to
/// serialise on that row: the implementation reads it through
/// <see cref="IChannelConnectionLocker.LockByIdAsync"/> and holds the lock across the Google call, which is
/// exactly why concurrent callers refresh <em>once</em> and never lose an update. The Google call is bounded
/// by the YouTube client's own timeout, so the lock is never held indefinitely.
/// </para>
/// <para>
/// <b>No transaction of the caller is required</b> — the jobs that call this own theirs — but the
/// implementation never leaves the row half-written: it writes through <c>SaveChanges</c> inside the
/// transaction it opens around lock → refresh → <c>MarkRefreshed</c> → save, or inside the caller's when one
/// is already open.
/// </para>
/// <para>
/// <b>Failure is a typed answer, never a mutation.</b> A missing or revoked connection is
/// <c>channel_reauth_required</c> (409 <c>ErrorCodes.ChannelReauthRequired</c>) with no Google call, and a
/// refused refresh (<c>invalid_grant</c>) travels back as the typed error the YouTube client already
/// produced, so the caller — 06.4.2-T1's expiry handling — decides the connection's status. This port never
/// marks a connection <c>expired</c>/<c>error</c>: only <c>MarkRefreshed</c> on success.
/// </para>
/// </remarks>
public interface ITokenService
{
    /// <summary>
    /// The usable access token of <paramref name="connectionId"/>, refreshed when fewer than five minutes
    /// remain.
    /// </summary>
    /// <param name="connectionId">The <c>channel_connections</c> row of the caller's own scope.</param>
    /// <param name="ct">Cancels the read, the lock wait and the refresh.</param>
    /// <returns>
    /// The access token and its expiry; <c>channel_reauth_required</c> when the row is missing or revoked;
    /// the YouTube client's own failure when Google refused the refresh (e.g. <c>invalid_grant</c>).
    /// </returns>
    Task<ErrorOr<ChannelAccessToken>> GetAccessTokenAsync(Guid connectionId, CancellationToken ct);
}
