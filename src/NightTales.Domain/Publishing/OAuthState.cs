using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// One single-use <c>oauth_states</c> row (06.1.2-T1, 06 README D3/D20): the connect attempt that binds an
/// anonymous callback to one user, one OAuth client and the browser that started it.
/// </summary>
/// <remarks>
/// <para>
/// The row is deliberately not <see cref="IUserOwned"/>: the callback has no principal, so its authentication is
/// the state <em>plus</em> the <c>__Host-nts_oauth</c> browser nonce, and neither binding alone completes a
/// connect (README D3/F01, YR10). Every read of it therefore goes through an explicit
/// <c>state_hash</c> + <c>user_id</c> predicate instead of the tenant filter, and the table carries no
/// <c>updated_at</c> bookkeeping and no <c>xmin</c> row version, because the row is never exposed with an ETag.
/// </para>
/// <para>
/// The state is single use: the callback claims it with one atomic statement that mirrors
/// <see cref="Claim"/> — <c>UPDATE … WHERE state_hash = @h AND used_at IS NULL AND expires_at &gt; @now
/// RETURNING …</c> committed in its own unit of work before any Google call. A crash right after that commit
/// burns the state and creates nothing: the user restarts the connect, which is harmless, while a replayable
/// state is not.
/// </para>
/// </remarks>
public sealed class OAuthState : AggregateRoot
{
    private OAuthState()
    {
    }

    /// <summary>The user who started the connect; the callback resolves them from this column, never from a token.</summary>
    public Guid UserId { get; private init; }

    /// <summary>The platform the connect is for. Only <see cref="Platform.YouTube"/> exists in this task.</summary>
    public Platform Platform { get; private init; }

    /// <summary>The SHA-256 hex of the 32-byte base64url state that was sent to Google; the claim's unique key.</summary>
    public string StateHash { get; private init; } = string.Empty;

    /// <summary>The PKCE code verifier under the token protector's <c>oauth.v1</c> purpose, never in clear.</summary>
    public string CodeVerifierProtected { get; private init; } = string.Empty;

    /// <summary>The OAuth client the connect was started with; the exchange must use this one, never the active id.</summary>
    public string OAuthClientId { get; private init; } = string.Empty;

    /// <summary>The refresh-token family of the live session that started the connect (its liveness is the callback's third check).</summary>
    public Guid RefreshFamilyId { get; private init; }

    /// <summary>The SHA-256 hex of the <c>__Host-nts_oauth</c> cookie value, so only the starting browser can finish the callback (YR10).</summary>
    public string BrowserNonceHash { get; private init; } = string.Empty;

    /// <summary>When the connect started.</summary>
    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>When the state stops being usable, <c>CreatedAt</c> plus the configured TTL (ten minutes).</summary>
    public DateTimeOffset ExpiresAt { get; private init; }

    /// <summary>When the callback claimed the row, or <see langword="null"/> while the state is still spendable.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>
    /// Creates the state row. The id is a UUIDv7 produced here, as every aggregate id is (09 README D3); the
    /// caller supplies the already-hashed state and browser nonce, the protected verifier and the resolve-time
    /// values, because hashing and protection are the application layer's ports.
    /// </summary>
    /// <param name="userId">The user who started the connect.</param>
    /// <param name="platform">The platform the connect is for.</param>
    /// <param name="stateHash">The SHA-256 hex of the state sent to Google; required.</param>
    /// <param name="codeVerifierProtected">The protected PKCE code verifier; required.</param>
    /// <param name="browserNonceHash">The SHA-256 hex of the browser nonce; required.</param>
    /// <param name="oauthClientId">The OAuth client the connect is pinned to; required.</param>
    /// <param name="refreshFamilyId">The refresh-token family of the session that started the connect.</param>
    /// <param name="now">The command's clock reading.</param>
    /// <param name="ttl">How long the state stays usable; must be positive.</param>
    /// <exception cref="ArgumentException">A hash, the protected verifier or the client id is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ttl"/> is zero or negative.</exception>
    public static OAuthState Create(
        Guid userId,
        Platform platform,
        string stateHash,
        string codeVerifierProtected,
        string browserNonceHash,
        string oauthClientId,
        Guid refreshFamilyId,
        DateTimeOffset now,
        TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifierProtected);
        ArgumentException.ThrowIfNullOrWhiteSpace(browserNonceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(oauthClientId);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "An OAuth state that expires at or before its creation moment could never be claimed.");
        }

        return new OAuthState
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Platform = platform,
            StateHash = stateHash,
            CodeVerifierProtected = codeVerifierProtected,
            BrowserNonceHash = browserNonceHash,
            OAuthClientId = oauthClientId,
            RefreshFamilyId = refreshFamilyId,
            CreatedAt = now,
            ExpiresAt = now + ttl,
        };
    }

    /// <summary>Whether the state can still be claimed at <paramref name="now"/>: unused and not expired.</summary>
    /// <param name="now">The instant to judge.</param>
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    /// <summary>
    /// Spends the state: the in-memory twin of the callback's atomic claim. Every later claim of the same row —
    /// a second callback, a replay, or the same callback after the ten-minute TTL — is refused.
    /// </summary>
    /// <param name="now">The instant the callback claims the state.</param>
    /// <exception cref="InvalidOperationException">The state was already claimed or has expired.</exception>
    public void Claim(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new InvalidOperationException(
                "An OAuth state is single use and expires: it cannot be claimed twice or after its expiry.");
        }

        UsedAt = now;
    }
}
