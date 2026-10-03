using NightTales.Domain.Common;

namespace NightTales.Domain.Assets;

/// <summary>
/// One stored file of an episode (00 overview §6 <c>assets</c>). This is the shell 09.2.2-T1 owns — the
/// columns and their types; the behaviour (<c>ScheduleRetention</c>, <c>MarkDeleted</c>) and the
/// retention index are 09.7.x's.
/// </summary>
public sealed class Asset : Entity, IUserOwned, IAuditable
{
    private Asset()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EpisodeId { get; private set; }

    public AssetKind Kind { get; private set; }

    /// <summary>The object-storage key, unique across the bucket.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public decimal? DurationSec { get; private set; }

    /// <summary>Lowercase SHA-256 hex of the stored bytes (<c>char(64)</c>).</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public DateTimeOffset? RetentionUntil { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Marks the row deleted once its object is gone (09.7.3-T1): called after the blob delete succeeded
    /// or the key was already missing, never before. A row that is already marked keeps its first instant,
    /// so a re-run is a no-op. <see cref="UpdatedAt"/> is stamped by the auditable interceptor in the same
    /// <c>SaveChanges</c>.
    /// </summary>
    /// <param name="now">The sweep's clock reading.</param>
    public void MarkDeleted(DateTimeOffset now) => DeletedAt ??= now;
}
