using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// The life cycle of one <c>channel_connections</c> row (06.1.2-T2, 06 README D3): stored as its snake_case
/// wire value (<c>active</c>, never <c>Active</c>) and guarded by <c>ck_channel_connections_status</c>, so the
/// C# member, the column and the <c>CHECK</c> cannot drift.
/// </summary>
public enum ChannelConnectionStatus
{
    /// <summary>A live connection: the only state an upload may use (the 06 README NFR-06-1 sibling rule).</summary>
    Active,

    /// <summary>
    /// Disconnected by the user (06.4.1-T1's <see cref="ChannelConnection.Revoke"/>): both token columns are
    /// <c>NULL</c> and the row no longer occupies <c>ux_channel_connections_account</c>.
    /// </summary>
    Revoked,

    /// <summary>
    /// Google reported the grant as no longer usable (06.2.3-T2 / 06.4.2-T1). The token columns are kept, so
    /// the next successful refresh through <see cref="ChannelConnection.MarkRefreshed"/> returns the row to
    /// <see cref="Active"/>.
    /// </summary>
    Expired,

    /// <summary>
    /// The last token attempt failed for a reason other than expiry (06.4.2-T1). Like
    /// <see cref="Expired"/>, the tokens survive so a later successful refresh recovers the connection.
    /// </summary>
    Error,
}

/// <summary>
/// One owned, publishable YouTube channel (06.1.2-T2, 06 README D3/D4/D5): the channel identity, the OAuth
/// client that minted the code, and both tokens as <c>ITokenProtector</c> ciphertext under the
/// <em>per-connection</em> purpose <c>tokens.v1/{Id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The row is <see cref="IUserOwned"/>, so 09 README D10's tenant filter applies to every read: only the owner
/// ever sees it, and the anonymous callback resolves the owner from the OAuth state before it reads the row.
/// </para>
/// <para>
/// <b>Constructing one.</b> <see cref="Connect"/> takes the id from the caller instead of minting it here,
/// because D4's protection purpose is <c>tokens.v1/{connectionId}</c>: the caller has to know the connection id
/// <em>before</em> it protects the tokens, so the id is a UUIDv7 the caller mints first
/// (<c>Guid.CreateVersion7()</c>) and this factory pins. A non-UUIDv7 id — <see cref="Guid.Empty"/> included —
/// is refused, so "the id existed before the ciphertext" stays a checked fact rather than a comment.
/// <see cref="DisplayName"/>/<see cref="ExternalAccountId"/> are nullable purely because
/// <see cref="PurgeYouTubeData"/> nulls them on an already-revoked row (the 13 §5.4 channel-scope deletion
/// duty); the two token columns are nullable because <see cref="Revoke"/> nulls them.
/// </para>
/// <para>
/// <b>Mutation.</b> Every setter is private, so the only ways into the row are the methods below and each of
/// them owns its columns: <see cref="Connect"/>/<see cref="Reconnect"/>/<see cref="MarkRefreshed"/> write the
/// token pair, <see cref="Revoke"/> is the only path that nulls the pair, <see cref="RefreshFromApi"/> writes
/// <see cref="DisplayName"/>/<see cref="LastVerifiedAt"/> only, and <see cref="PurgeYouTubeData"/> is the only
/// path that nulls the identity. <c>revoked</c> is terminal for every method, <see cref="Reconnect"/> included:
/// a revoked row has lost its tokens and left the partial unique index, so the next connect of that channel is a
/// <em>new</em> row rather than a revival of this one, and a purged row has lost the identity a reconnect would
/// rewrite. The caller's locked read excludes revoked rows; <see cref="Reconnect"/>'s own guard is what keeps the
/// aggregate and that read in step (06.1.2-T2 failure table).
/// </para>
/// <para>
/// The type deliberately does not override <c>ToString</c>: the default (the type name) cannot leak a token,
/// the channel title, the Google channel id or the pinned client id into a log line or an exception message.
/// The ciphertexts are never put into an <see cref="InvalidOperationException"/> either — the guards name the
/// method and the state only.
/// </para>
/// <para>
/// <see cref="Connect"/>, <see cref="Reconnect"/> and <see cref="Revoke"/> additionally raise
/// <see cref="ChannelConnectionChanged"/>, the event the interceptor stores as <c>channel_connection_changed.v1</c>
/// beside the row in the same unit of work (06 README D13); ids and the status only, never a token or a title.
/// Those three are the only raisers: <see cref="MarkExpired"/>, <see cref="MarkError"/>, <see cref="MarkRefreshed"/>,
/// <see cref="RefreshFromApi"/> and <see cref="PurgeYouTubeData"/> write no event of their own.
/// </para>
/// <para>
/// The row carries no <c>xmin</c> row version and no <c>updated_at</c> bookkeeping: 06 README D15 needs no
/// <c>If-Match</c> on this resource, and the migration creates neither column
/// (<see cref="AggregateRoot.Version"/> is not mapped).
/// </para>
/// </remarks>
public sealed class ChannelConnection : AggregateRoot, IUserOwned
{
    private ChannelConnection()
    {
    }

    /// <summary>The owner of the connection; the tenant filter of 09 README D10 reads this column.</summary>
    public Guid UserId { get; private init; }

    /// <summary>The platform the channel publishes to. Only <see cref="Platform.YouTube"/> exists in this task.</summary>
    public Platform Platform { get; private init; }

    /// <summary>
    /// Google's opaque channel id (never a display name). <see langword="null"/> only after
    /// <see cref="PurgeYouTubeData"/> nulled an already-revoked row.
    /// </summary>
    public string? ExternalAccountId { get; private set; }

    /// <summary>
    /// The channel title as Google reported it. Refreshed by 06.4.2-T1's 25-day check through
    /// <see cref="RefreshFromApi"/>; <see langword="null"/> only after <see cref="PurgeYouTubeData"/>.
    /// </summary>
    public string? DisplayName { get; private set; }

    /// <summary>The row's state; <see cref="ChannelConnectionStatus.Active"/> is the only publishable one.</summary>
    public ChannelConnectionStatus Status { get; private set; }

    /// <summary>
    /// The access token under <c>ITokenProtector</c> purpose <c>tokens.v1/{Id}</c>, or <see langword="null"/>
    /// while the row is <see cref="ChannelConnectionStatus.Revoked"/> (only <see cref="Revoke"/> and
    /// <see cref="PurgeYouTubeData"/> null it).
    /// </summary>
    public string? AccessTokenEnc { get; private set; }

    /// <summary>The refresh token under the same purpose, with the same null rule as <see cref="AccessTokenEnc"/>.</summary>
    public string? RefreshTokenEnc { get; private set; }

    /// <summary>
    /// When the current access token stops working. Written by <see cref="Connect"/>, <see cref="Reconnect"/>
    /// and <see cref="MarkRefreshed"/>, and moved to the disconnect moment by <see cref="Revoke"/>, whose
    /// ciphertext is gone.
    /// </summary>
    public DateTimeOffset TokenExpiresAt { get; private set; }

    /// <summary>The granted scope string exactly as Google returned it (the two YouTube URIs, space separated).</summary>
    public string Scope { get; private set; } = string.Empty;

    /// <summary>
    /// The OAuth client that minted the code this connection was built from (06 README D5). Pinned at
    /// <see cref="Connect"/> and re-pinned only by <see cref="Reconnect"/>, to the client of the state that
    /// actually minted the new code (YR11) — never to "the currently active client", which is what lets a
    /// retiring client be rotated without a reconnect per connection.
    /// </summary>
    public string OAuthClientId { get; private set; } = string.Empty;

    /// <summary>
    /// When the connection was last confirmed to work: set at <see cref="Connect"/>/<see cref="Reconnect"/>, by
    /// every successful token rotation (<see cref="MarkRefreshed"/>) and by 06.4.2-T1's 25-day Authorized-Data
    /// check (<see cref="RefreshFromApi"/>). <see langword="null"/> only on a row that never got verified,
    /// which cannot happen through these factories.
    /// </summary>
    public DateTimeOffset? LastVerifiedAt { get; private set; }

    /// <summary>When the connection was first created; a reconnect keeps it (one row, one <c>connected_at</c>).</summary>
    public DateTimeOffset ConnectedAt { get; private init; }

    /// <summary>When the user disconnected the row; <see langword="null"/> while it is not <c>revoked</c>.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>
    /// Creates the connection of one successful callback (06 README D3/D4). The caller has already minted the
    /// UUIDv7 id, protected both tokens under <c>tokens.v1/{id}</c> and relayed the channel identity from
    /// <c>channels.list</c>, so this factory never invents an id and never sees a plaintext token.
    /// </summary>
    /// <param name="id">The connection id the ciphertext was protected under; a UUIDv7 minted by the caller.</param>
    /// <param name="userId">The owner, resolved from the OAuth state rather than from a principal.</param>
    /// <param name="platform">The platform the connect was started for.</param>
    /// <param name="externalAccountId">Google's channel id; required.</param>
    /// <param name="displayName">The channel title Google reported; required.</param>
    /// <param name="accessTokenEnc">The protected access token; required.</param>
    /// <param name="refreshTokenEnc">The protected refresh token; required.</param>
    /// <param name="tokenExpiresAt">When the access token stops working.</param>
    /// <param name="scope">The granted scope string; required.</param>
    /// <param name="oauthClientId">The client that minted the code, pinned for every later refresh; required.</param>
    /// <param name="now">The command's clock reading: the connect moment, this row's <c>connected_at</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is not a UUIDv7, or an identity/token/scope/client value is blank.
    /// </exception>
    public static ChannelConnection Connect(
        Guid id,
        Guid userId,
        Platform platform,
        string externalAccountId,
        string displayName,
        string accessTokenEnc,
        string refreshTokenEnc,
        DateTimeOffset tokenExpiresAt,
        string scope,
        string oauthClientId,
        DateTimeOffset now)
    {
        if (!IsUuidV7(id))
        {
            throw new ArgumentException(
                "A connection id must be the UUIDv7 the caller minted before it protected the tokens under tokens.v1/{connectionId} (06 README D4).",
                nameof(id));
        }

        RequireText(externalAccountId, nameof(externalAccountId));
        RequireText(displayName, nameof(displayName));
        RequireText(accessTokenEnc, nameof(accessTokenEnc));
        RequireText(refreshTokenEnc, nameof(refreshTokenEnc));
        RequireText(scope, nameof(scope));
        RequireText(oauthClientId, nameof(oauthClientId));

        var connection = new ChannelConnection
        {
            Id = id,
            UserId = userId,
            Platform = platform,
            ExternalAccountId = externalAccountId,
            DisplayName = displayName,
            Status = ChannelConnectionStatus.Active,
            AccessTokenEnc = accessTokenEnc,
            RefreshTokenEnc = refreshTokenEnc,
            TokenExpiresAt = tokenExpiresAt,
            Scope = scope,
            OAuthClientId = oauthClientId,
            LastVerifiedAt = now,
            ConnectedAt = now,
        };

        // 06 README D13: a connect publishes one `channel_connection_changed.v1` row in the caller's unit of
        // work. The aggregate is the only place that can raise it — AggregateRoot.Raise is protected and there
        // is no outbox writer port — and OutboxSaveChangesInterceptor turns it into the outbox row on save.
        connection.Raise(new ChannelConnectionChanged(connection.Id, connection.UserId, connection.Status, now));

        return connection;
    }

    /// <summary>
    /// Rewrites a still-live row after a second successful callback for the same channel (06 README D3, YR11):
    /// the id, the platform, the owner and <c>connected_at</c> stay, the tokens are replaced by the new pair
    /// (protected under that same id), the client is re-pinned to the state's client, and the row becomes
    /// <see cref="ChannelConnectionStatus.Active"/> again — the same in-place rewrite that recovers an
    /// <c>expired</c>/<c>error</c> grant.
    /// </summary>
    /// <remarks>
    /// The row must still be a live one: a <c>revoked</c> row left <c>ux_channel_connections_account</c> and lost
    /// its tokens, so the caller writes a <em>new</em> connection for that channel instead of reviving this one,
    /// and a purged row has no identity left to rewrite. The caller's locked read
    /// (<c>LockLiveByAccountAsync</c>) already excludes revoked rows; this guard is what keeps the aggregate and
    /// that read in step (06.1.2-T2 failure table).
    /// </remarks>
    /// <param name="displayName">The title Google reported this time; a renamed channel wins.</param>
    /// <param name="accessTokenEnc">The new protected access token; required.</param>
    /// <param name="refreshTokenEnc">The new protected refresh token; required.</param>
    /// <param name="tokenExpiresAt">When the new access token stops working.</param>
    /// <param name="scope">The scope string of the new grant; required.</param>
    /// <param name="oauthClientId">The client that minted the new code (YR11); required.</param>
    /// <param name="now">The command's clock reading; also the new <c>last_verified_at</c>.</param>
    /// <exception cref="ArgumentException">A token, the title, the scope or the client id is blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// The row is <c>revoked</c>, or it has already lost its identity to <see cref="PurgeYouTubeData"/>.
    /// </exception>
    public void Reconnect(
        string displayName,
        string accessTokenEnc,
        string refreshTokenEnc,
        DateTimeOffset tokenExpiresAt,
        string scope,
        string oauthClientId,
        DateTimeOffset now)
    {
        RequireText(displayName, nameof(displayName));
        RequireText(accessTokenEnc, nameof(accessTokenEnc));
        RequireText(refreshTokenEnc, nameof(refreshTokenEnc));
        RequireText(scope, nameof(scope));
        RequireText(oauthClientId, nameof(oauthClientId));

        EnsureReconnectable(nameof(Reconnect));

        DisplayName = displayName;
        AccessTokenEnc = accessTokenEnc;
        RefreshTokenEnc = refreshTokenEnc;
        TokenExpiresAt = tokenExpiresAt;
        Scope = scope;
        OAuthClientId = oauthClientId;
        Status = ChannelConnectionStatus.Active;
        LastVerifiedAt = now;
        RevokedAt = null;

        // Same D13 obligation as Connect: a reconnect is a state change, so it publishes its own
        // `channel_connection_changed.v1` row (status active) in the caller's unit of work.
        Raise(new ChannelConnectionChanged(Id, UserId, Status, now));
    }

    /// <summary>
    /// Records that Google no longer accepts the grant (06.2.3-T2 / 06.4.2-T1). The tokens stay, so a later
    /// successful refresh through <see cref="MarkRefreshed"/> recovers the connection.
    /// </summary>
    /// <param name="now">
    /// The command's clock reading. The transition stores no timestamp of its own: only <see cref="Revoke"/>
    /// and the token writers move <c>token_expires_at</c>, and only <see cref="Revoke"/> sets <c>revoked_at</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">The row is <c>revoked</c>, which is terminal.</exception>
    public void MarkExpired(DateTimeOffset now)
    {
        EnsureNotRevoked(nameof(MarkExpired));
        _ = now;

        Status = ChannelConnectionStatus.Expired;
    }

    /// <summary>
    /// Records that the last token attempt failed for a reason other than expiry (06.4.2-T1). Like
    /// <see cref="MarkExpired"/>, the tokens stay; a later refresh recovers the connection.
    /// </summary>
    /// <param name="now">The command's clock reading; see <see cref="MarkExpired"/> for why it is not stored.</param>
    /// <exception cref="InvalidOperationException">The row is <c>revoked</c>, which is terminal.</exception>
    public void MarkError(DateTimeOffset now)
    {
        EnsureNotRevoked(nameof(MarkError));
        _ = now;

        Status = ChannelConnectionStatus.Error;
    }

    /// <summary>
    /// Writes a rotated token pair (06.4.2-T1; called from inside <c>ITokenService.GetAccessTokenAsync</c>, under
    /// the row lock). This is the only path outside <see cref="Connect"/>/<see cref="Reconnect"/> that writes the
    /// token columns, and it is what returns an <c>expired</c>/<c>error</c> row to
    /// <see cref="ChannelConnectionStatus.Active"/>.
    /// </summary>
    /// <param name="accessTokenEnc">The new protected access token; required.</param>
    /// <param name="refreshTokenEnc">The new protected refresh token; required.</param>
    /// <param name="tokenExpiresAt">When the new access token stops working.</param>
    /// <param name="now">The refresh moment; also the new <c>last_verified_at</c>.</param>
    /// <exception cref="ArgumentException">A token is blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// The row is <c>revoked</c>: a revoked connection is never revived — the next connect of its channel writes a
    /// new row — so rotating the stored pair must not bring this one back to <c>active</c>.
    /// </exception>
    public void MarkRefreshed(
        string accessTokenEnc,
        string refreshTokenEnc,
        DateTimeOffset tokenExpiresAt,
        DateTimeOffset now)
    {
        RequireText(accessTokenEnc, nameof(accessTokenEnc));
        RequireText(refreshTokenEnc, nameof(refreshTokenEnc));
        EnsureNotRevoked(nameof(MarkRefreshed));

        AccessTokenEnc = accessTokenEnc;
        RefreshTokenEnc = refreshTokenEnc;
        TokenExpiresAt = tokenExpiresAt;
        Status = ChannelConnectionStatus.Active;
        LastVerifiedAt = now;
        RevokedAt = null;
    }

    /// <summary>
    /// Records the channel metadata Google returned in 06.4.2-T1's 25-day Authorized-Data check. Writes
    /// <see cref="DisplayName"/> and <see cref="LastVerifiedAt"/> only: it never touches <see cref="Status"/> or
    /// the tokens, so a metadata refresh can never resurrect or break a connection on its own.
    /// </summary>
    /// <param name="displayName">The title Google returned; required.</param>
    /// <param name="now">The check moment; the new <c>last_verified_at</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="displayName"/> is blank.</exception>
    public void RefreshFromApi(string displayName, DateTimeOffset now)
    {
        RequireText(displayName, nameof(displayName));

        DisplayName = displayName;
        LastVerifiedAt = now;
    }

    /// <summary>
    /// Disconnects the channel (06.4.1-T1): both ciphertexts are dropped, the token's expiry is moved to
    /// <paramref name="now"/>, and the row leaves <c>ux_channel_connections_account</c>, so another user can
    /// connect the same Google channel afterwards. The identity columns stay until the deletion engine's
    /// channel-scope purge nulls them (<see cref="PurgeYouTubeData"/>).
    /// </summary>
    /// <param name="now">The disconnect moment; both <c>revoked_at</c> and <c>token_expires_at</c>.</param>
    /// <exception cref="InvalidOperationException">The row is already <c>revoked</c> (the caller checks first).</exception>
    public void Revoke(DateTimeOffset now)
    {
        EnsureNotRevoked(nameof(Revoke));

        Status = ChannelConnectionStatus.Revoked;
        AccessTokenEnc = null;
        RefreshTokenEnc = null;
        TokenExpiresAt = now;
        RevokedAt = now;

        // Same D13 obligation as Connect and Reconnect (06.4.1-T1 §Design step 8): a disconnect is a state change, so
        // it publishes its own `channel_connection_changed.v1` row (status revoked) in the caller's unit of work. The
        // aggregate is the only place that can raise it — AggregateRoot.Raise is protected and there is no outbox
        // writer port — and OutboxSaveChangesInterceptor turns it into the outbox row on save.
        Raise(new ChannelConnectionChanged(Id, UserId, Status, now));
    }

    /// <summary>
    /// The 13 §5.4 channel-scope deletion duty (06.4.3-T1): the identity and both tokens become <c>NULL</c>, with
    /// no <c>"deleted"</c> placeholder, because the columns accept <c>NULL</c> and the row is already outside the
    /// partial unique index. Only an already-disconnected row may be purged — the purge is the last step of a
    /// <see cref="Revoke"/> that 06.4.1-T1 committed earlier.
    /// </summary>
    /// <param name="now">
    /// The purge moment. Nothing is stored from it: <c>revoked_at</c> already holds the disconnect moment, which
    /// a later purge must not overwrite.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The row is not <c>revoked</c>: purging a live connection would delete a channel the user is still using.
    /// </exception>
    public void PurgeYouTubeData(DateTimeOffset now)
    {
        if (Status != ChannelConnectionStatus.Revoked)
        {
            throw new InvalidOperationException(
                "Only a revoked connection may be purged: the deletion engine runs the channel-scope purge after the disconnect committed (06.4.1-T1 before 06.4.3-T1).");
        }

        _ = now;

        AccessTokenEnc = null;
        RefreshTokenEnc = null;
        ExternalAccountId = null;
        DisplayName = null;
    }

    /// <summary>
    /// Whether <paramref name="id"/> carries the UUIDv7 version nibble: the shape
    /// <c>Guid.CreateVersion7()</c> produces, and the only shape whose id existed before the tokens were
    /// protected under it.
    /// </summary>
    private static bool IsUuidV7(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        return id.TryWriteBytes(bytes, bigEndian: true, out _) && (bytes[6] >> 4) == 7;
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A channel connection value that the schema requires cannot be blank.", parameterName);
        }
    }

    /// <summary>
    /// The guard every state change shares: a revoked row's token columns are <c>NULL</c> and its
    /// <c>(platform, external_account_id)</c> pair must stay out of the partial unique index, so no transition may
    /// move its status off <c>revoked</c>. <see cref="Reconnect"/> answers to <see cref="EnsureReconnectable"/>
    /// instead, which refuses a revoked row for the same reason and a purged one besides.
    /// </summary>
    private void EnsureNotRevoked(string operation)
    {
        if (Status == ChannelConnectionStatus.Revoked)
        {
            throw new InvalidOperationException(
                $"A revoked channel connection is terminal: {operation} refuses it, and the next connect of the channel writes a new row.");
        }
    }

    /// <summary>
    /// The guard <see cref="Reconnect"/> owns alone: the row it rewrites must still be a live one. A
    /// <c>revoked</c> row left <c>ux_channel_connections_account</c> and lost its tokens, and a row purged by
    /// <see cref="PurgeYouTubeData"/> lost its identity, so neither is a row a reconnect may write — the caller's
    /// locked read (<c>LockLiveByAccountAsync</c>, which filters <c>status &lt;&gt; 'revoked'</c>) already answers
    /// <see langword="null"/> for both and inserts a new row instead (06.1.2-T2 failure table).
    /// </summary>
    private void EnsureReconnectable(string operation)
    {
        if (Status == ChannelConnectionStatus.Revoked || ExternalAccountId is null)
        {
            throw new InvalidOperationException(
                $"A revoked or purged channel connection is never reconnected: {operation} refuses it, and the caller writes a new connection for the channel instead.");
        }
    }
}
