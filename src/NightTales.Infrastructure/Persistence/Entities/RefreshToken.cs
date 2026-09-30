using System.Net;

namespace NightTales.Infrastructure.Persistence.Entities;

/// <summary>
/// One issued refresh token (09.3.2-T1 Design, BD1/BD3): a rotating row inside a family. Only the
/// successor's hash is ever stored, and the row records the whole revocation history.
/// </summary>
/// <remarks>
/// <para>
/// Infrastructure-only, like <see cref="Outbox.OutboxMessage"/> and
/// <see cref="Idempotency.IdempotencyKey"/>: nothing in Application may see the table, and every mutation
/// after the first insert is a set-based statement in <c>EfRefreshTokenService</c> (the rotation's
/// <c>UPDATE</c>, the family revocation, the re-authentication counter) rather than a tracked-entity
/// update. The first row of a family is the one this type creates.
/// </para>
/// <para>
/// It deliberately does <em>not</em> implement <c>IUserOwned</c>: the tenant filter would make an
/// anonymous refresh scope (which is exactly the caller that refreshes) read no row at all, and the
/// security reads here are keyed by the unique <c>token_hash</c> or the <c>family_id</c> under an
/// explicit predicate.
/// </para>
/// </remarks>
public sealed class RefreshToken
{
    /// <summary>The longest stored <c>User-Agent</c>; the column is <c>text</c> and the value is client input.</summary>
    public const int UserAgentMaxLength = 512;

    private RefreshToken()
    {
    }

    /// <summary>The row id (UUIDv7, generated here, never by the database — D3/D8).</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning user; <c>ON DELETE CASCADE</c>.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The session family every rotation stays inside.</summary>
    public Guid FamilyId { get; private set; }

    /// <summary><c>SHA-256</c> of the token as lowercase hex — the only stored form.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>When the row was written, from the scope's clock.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// The last time the row was used (the family's root row gets <c>created_at</c>, so the idle clock
    /// starts at creation — [RULED — README §B10 WT33]).
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>The absolute expiry of the row; a rotation can never move it past the family's ceiling.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the row stopped being usable, or null while it is live.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Why it stopped being usable; see <see cref="RevocationReason"/>.</summary>
    public RevocationReason? RevokedReason { get; private set; }

    /// <summary>The successor that replaced this row, when it was rotated.</summary>
    public Guid? ReplacedById { get; private set; }

    /// <summary>When the family last passed a re-authentication (BD4; copied to every successor).</summary>
    public DateTimeOffset? ReauthenticatedAt { get; private set; }

    /// <summary>The caller's address when it could be resolved; null otherwise.</summary>
    public IPAddress? Ip { get; private set; }

    /// <summary>The caller's <c>User-Agent</c>, truncated to <see cref="UserAgentMaxLength"/>.</summary>
    public string? UserAgent { get; private set; }

    /// <summary>The durable wrong-re-authentication counter of the family (BD4, WT32).</summary>
    public short ReauthAttempts { get; private set; }

    /// <summary>When the last wrong re-authentication attempt happened.</summary>
    public DateTimeOffset? ReauthLastAttemptAt { get; private set; }

    /// <summary>Creates a family's first row; the caller adds it to the unit of work.</summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="tokenHash">The hash of the token handed to the client.</param>
    /// <param name="now">The instant the family starts; also the row's first <c>last_used_at</c> (WT33).</param>
    /// <param name="expiresAt">The family's absolute expiry.</param>
    /// <param name="ip">The caller's address, or null.</param>
    /// <param name="userAgent">The caller's <c>User-Agent</c>, already truncated, or null.</param>
    public static RefreshToken Start(
        Guid userId,
        string tokenHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        IPAddress? ip,
        string? userAgent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        return new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = Guid.CreateVersion7(),
            TokenHash = tokenHash,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = expiresAt,
            Ip = ip,
            UserAgent = userAgent,
        };
    }
}
