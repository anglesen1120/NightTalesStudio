using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The one refresh-under-lock path of a channel connection (06.1.2-T2 §Design "Token access", 06 README D9):
/// the live access token of one connection id, refreshed at Google when it is about to expire — a single
/// implementation, because 06.2.3-T2's upload and 06.4.2-T1's health read need the same lock and the same
/// re-read against the same row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequence.</b> Lock the row (<see cref="IChannelConnectionLocker.LockByIdAsync"/>, which re-reads the
/// committed values after the lock) → refuse a revoked or missing row → return the stored token when it has
/// more than five minutes left → otherwise refresh with the connection's <em>pinned</em> client
/// (<c>connection.OAuthClientId</c>, 06 README D5) and write the rotated pair through
/// <see cref="ChannelConnection.MarkRefreshed"/>.
/// </para>
/// <para>
/// <b>Single refresh by construction.</b> The lock is held across the Google call, so a second caller blocks,
/// then re-reads the row the first one committed and finds the token fresh: one expiry spends one refresh, and
/// no caller can observe a half-written pair.
/// </para>
/// <para>
/// <b>Transaction.</b> The lock is worthless without one, and this port is called from jobs that own none
/// (06.2.3-T2's upload, 06.4.2-T1's health), so it opens its own <c>ReadCommitted</c> transaction exactly like
/// <c>EfUserActionTokenService</c>'s send-time mint: a caller that already owns a transaction — a handler inside
/// its unit of work — is joined instead, and only a transaction this type opened is committed here (a rollback
/// of a joined unit is the caller's to make). The trade-off is deliberate: a pooled connection stays pinned to
/// the row lock for the duration of the Google call, which is bounded by <c>YouTube:Http:TimeoutSeconds</c> on
/// the named <c>youtube-oauth</c> client.
/// </para>
/// <para>
/// <b>A job pins its scope first.</b> The locked read is tenant-filtered, so a Worker job calls
/// <c>ISystemContext.For(connection.UserId)</c> before it resolves this service — the same rule every
/// user-owned job follows (<c>SendEmailJob</c>, <c>PollRenderJob</c>) — and an unpinned scope answers
/// <c>channel_reauth_required</c> instead of reading a row it does not own.
/// </para>
/// <para>
/// <b>Failure is the caller's.</b> A revoked row, a missing row and an unreadable cipher — a foreign purpose,
/// a tampered payload, another key ring — are one uniform <c>channel_reauth_required</c> answer with no Google
/// call; a refused refresh travels as the client's own typed failure (a 4xx token answer, <c>invalid_grant</c>
/// included, is <c>oauth_exchange_failed</c>) so 06.2.3-T2/06.4.2-T1 apply their own expiry handling. This port
/// never moves <see cref="ChannelConnection.Status"/> on a failure: only a success writes, and only through
/// <see cref="ChannelConnection.MarkRefreshed"/>. No token, no cipher and no channel identity reaches a log
/// line or an exception message (06 README D17).
/// </para>
/// </remarks>
/// <param name="database">
/// The scope's write model: it carries the transaction, the locked row and the one <c>SaveChanges</c> that
/// writes the rotated pair.
/// </param>
/// <param name="locker">The row lock of <c>channel_connections</c> — the only <c>FOR UPDATE</c> in the solution.</param>
/// <param name="clientResolver">Resolves the client the connection is pinned to (06.1.2-T1's port).</param>
/// <param name="youTube">The Google client; only <see cref="IYouTubeClient.RefreshAsync"/> is used here.</param>
/// <param name="protector">Reads and writes the ciphers under <c>tokens.v1/{connectionId}</c> (06 README D4).</param>
/// <param name="clock">The scope's clock: every expiry comparison and every written instant comes from it.</param>
public sealed class TokenService(
    AppDbContext database,
    IChannelConnectionLocker locker,
    IOAuthClientResolver clientResolver,
    IYouTubeClient youTube,
    ITokenProtector protector,
    IClock clock) : ITokenService
{
    /// <summary>
    /// How much life an access token must still have to be handed out as it is: below it the token is refreshed
    /// first, so a caller never starts a long upload with a token that expires mid-flight (06.1.2-T2 §"Token
    /// access"). The boundary is inclusive — exactly five minutes left is already refreshed.
    /// </summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<ErrorOr<ChannelAccessToken>> GetAccessTokenAsync(
        Guid connectionId,
        CancellationToken ct)
    {
        // The lock has to outlive the read-refresh-write sequence, so it needs a transaction: this joins the
        // caller's when there is one and opens its own when there is not (`EfUserActionTokenService`'s rule).
        await using var transaction = database.Database.CurrentTransaction is null
            ? await database.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                .ConfigureAwait(false)
            : null;

        var connection = await locker.LockByIdAsync(connectionId, ct).ConfigureAwait(false);

        // A missing row, a revoked row and a row this scope's tenant filter cannot see are one answer: the
        // remedy is a reconnect, never a retry. No Google call runs for any of them.
        if (connection is null || connection.Status == ChannelConnectionStatus.Revoked)
        {
            return ReauthRequired();
        }

        var purpose = connectionId.ToString();
        var now = clock.UtcNow;

        if (connection.TokenExpiresAt >= now + RefreshWindow)
        {
            // Fresh enough: no Google call. The cipher is read under this connection's own purpose, so a value
            // protected for another connection cannot answer here.
            if (connection.AccessTokenEnc is not { } accessCipher)
            {
                return ReauthRequired();
            }

            return protector.TryUnprotect(purpose, accessCipher, out var current)
                ? new ChannelAccessToken(current, connection.TokenExpiresAt)
                : ReauthRequired();
        }

        if (connection.RefreshTokenEnc is not { } refreshCipher)
        {
            return ReauthRequired();
        }

        if (!protector.TryUnprotect(purpose, refreshCipher, out var refreshToken))
        {
            return ReauthRequired();
        }

        // The pinned client (D5), never `ActiveClientId`: a client rotation must not break a connection that was
        // granted through the one before it.
        var client = await clientResolver.ResolveAsync(connection.OAuthClientId, ct).ConfigureAwait(false);
        if (client.IsError)
        {
            // An unconfigured or unreadable client is a host problem, not a dead grant: it travels as it is
            // (`oauth_client_unknown` / `oauth_client_secret_unavailable`) and `Status` stays untouched.
            return client.Errors;
        }

        var refreshed = await youTube.RefreshAsync(client.Value, refreshToken, ct).ConfigureAwait(false);
        if (refreshed.IsError)
        {
            // `invalid_grant` — and a Google outage — travel as the client's typed failure (06 README D9): the
            // caller decides between `expired`, `error` and a retry. Nothing is written here.
            return refreshed.Errors;
        }

        var tokens = refreshed.Value;

        // Google usually answers a refresh without a new refresh token; the stored grant then stays the one this
        // connection keeps refreshing with, and a rotated one replaces it in the same write.
        connection.MarkRefreshed(
            protector.Protect(purpose, tokens.AccessToken),
            tokens.RefreshToken is { Length: > 0 } rotated
                ? protector.Protect(purpose, rotated)
                : refreshCipher,
            tokens.ExpiresAt,
            clock.UtcNow);

        // Still inside the lock: the pair and the transaction commit together, so the next caller reads either
        // the old expiry (and waits on the lock) or the rotated pair — never a half-written row.
        await database.SaveChangesAsync(ct).ConfigureAwait(false);

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        return new ChannelAccessToken(tokens.AccessToken, tokens.ExpiresAt);
    }

    /// <summary>
    /// The uniform answer of every dead end this port can reach: no row, a revoked row, or a cipher that cannot
    /// be read under the connection's own purpose. It names the caller's situation only — never a cipher, a
    /// channel identity or the reason a value failed to unprotect (<see cref="ITokenProtector"/> logs that
    /// without the value).
    /// </summary>
    private static Error ReauthRequired() => Error.NotFound(
        ErrorCodes.ChannelReauthRequired,
        "The channel connection must be reconnected before it can be used again.");
}
