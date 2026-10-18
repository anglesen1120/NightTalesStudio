using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Assets;

/// <summary>
/// One stored file of an episode (00 overview §6 <c>assets</c>). This is the shell 09.2.2-T1 owns — the
/// columns and their types; the behaviour (<c>ScheduleRetention</c>, <c>MarkDeleted</c>) and the
/// retention index are 09.7.x's.
/// </summary>
public sealed class Asset : Entity, IUserOwned, IAuditable
{
    /// <summary>Length of a SHA-256 hash in lowercase hex (<c>char(64)</c>).</summary>
    private const int Sha256HexLength = 64;

    /// <summary>The mapped length of <see cref="StorageKey"/> (<c>varchar(512)</c>, 09.2.2 plan §3).</summary>
    public const int MaxStorageKeyLength = 512;

    private Asset()
    {
    }

    /// <summary>
    /// Records one stored file of an episode (BD7 step 8, BD10): the ingest of 09.6.3-T2 creates a video or subtitle
    /// row from the verified bytes, and 09.7.1-T1/09.7.3-T1 advance the row afterwards. Nothing here touches the
    /// blob store — the caller writes the object first and only then records it, which is why an <c>assets</c> row
    /// always names bytes that are already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field is validated rather than trusted: the row is what every later reader resolves a blob through
    /// (<c>FinalVideoAssetId</c>, the retention sweep, the review read model), so a blank key or a truncated hash
    /// would outlive the job that wrote it. <paramref name="sha256"/> is the lowercase hex the download verified
    /// against the node's own <c>output.video.sha256</c> (05 D10, H1), and
    /// <paramref name="retentionUntil"/> is the 09.7.3-T1 retention instant (<c>Assets:UnapprovedRetentionDays</c>)
    /// for an asset whose episode is not approved yet.
    /// </para>
    /// <para>
    /// The timestamp fields are written here so the row carries the clock reading the caller decided on; the
    /// auditable interceptor only fills <c>created_at</c> while it is still default and always refreshes
    /// <c>updated_at</c> (09 W3 CR-38). <paramref name="durationSec"/> is optional: the video's length when the
    /// adapter reported one, otherwise <c>null</c>.
    /// </para>
    /// </remarks>
    /// <param name="id">The row's id, chosen by the caller so the same ingest re-run can name it again.</param>
    /// <param name="userId">The owning tenant (<c>IUserOwned</c>, the D10 filter).</param>
    /// <param name="episodeId">The episode the file belongs to.</param>
    /// <param name="kind">Video or subtitle.</param>
    /// <param name="storageKey">The object key in the media bucket (unique across it).</param>
    /// <param name="contentType">The stored content type; the media bucket serves only the two wired types.</param>
    /// <param name="sizeBytes">The verified byte count.</param>
    /// <param name="durationSec">The length in seconds when it is known.</param>
    /// <param name="sha256">The verified lowercase SHA-256 hex.</param>
    /// <param name="retentionUntil">When 09.7.3-T1 may delete an unapproved asset.</param>
    /// <param name="now">The Worker's clock reading.</param>
    /// <returns>The row, or the 400 of a value the invariants refuse.</returns>
    public static ErrorOr<Asset> Create(
        Guid id,
        Guid userId,
        Guid episodeId,
        AssetKind kind,
        string storageKey,
        string contentType,
        long sizeBytes,
        decimal? durationSec,
        string sha256,
        DateTimeOffset retentionUntil,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(sha256);

        if (id == Guid.Empty || userId == Guid.Empty || episodeId == Guid.Empty)
        {
            return Error.Validation(
                "asset_ids_required",
                "An asset is stored for a tenant and an episode (BD10).");
        }

        if (!Enum.IsDefined(kind))
        {
            return Error.Validation("asset_kind_unknown", "An asset is a video or a subtitle track.");
        }

        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > MaxStorageKeyLength)
        {
            return Error.Validation(
                "asset_storage_key_invalid",
                "An asset names the object key it was stored under (BD10).");
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return Error.Validation("asset_content_type_required", "A stored asset carries its content type.");
        }

        if (sizeBytes < 0)
        {
            return Error.Validation("asset_size_invalid", "A stored asset has a byte count.");
        }

        if (durationSec is < 0)
        {
            return Error.Validation("asset_duration_invalid", "A duration is zero or more seconds.");
        }

        if (!IsSha256Hex(sha256))
        {
            return Error.Validation(
                "asset_sha256_invalid",
                "An asset carries the lowercase SHA-256 hex of the bytes it stored (BD7).");
        }

        if (retentionUntil <= now)
        {
            return Error.Validation(
                "asset_retention_invalid",
                "An unapproved asset is retained from the instant it was stored (09.7.3-T1).");
        }

        return new Asset
        {
            Id = id,
            UserId = userId,
            EpisodeId = episodeId,
            Kind = kind,
            StorageKey = storageKey,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            DurationSec = durationSec,
            Sha256 = sha256,
            RetentionUntil = retentionUntil,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a well-formed SHA-256 hash: 64 lowercase hex digits, the form
    /// <c>RenderJob</c> stores and the media bucket's key derivation re-checks (BD10).
    /// </summary>
    /// <param name="value">The candidate hash.</param>
    /// <returns><c>true</c> when it is exactly 64 lowercase hex digits.</returns>
    private static bool IsSha256Hex(string value)
    {
        if (value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHexDigit = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHexDigit)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Re-stamps when 09.7.3-T1's retention sweep may remove this row (WT56): the owner's approval of the
    /// episode overwrites the ingest's shorter unapproved window with the approved one.
    /// </summary>
    /// <remarks>
    /// The write is unconditional — an existing instant is replaced, never kept, and an earlier instant wins
    /// just as a later one does — because the two writers (<c>Assets:UnapprovedRetentionDays</c> at ingest,
    /// <c>Assets:RetentionDays</c> at approve) are phases of one lifetime rather than competing claims. A row
    /// that never gets a stamp is never swept (11 D20 H9). <see cref="UpdatedAt"/> is stamped by the auditable
    /// interceptor in the same <c>SaveChanges</c>.
    /// </remarks>
    /// <param name="retentionUntil">The new retention instant, from the caller's clock.</param>
    public void ScheduleRetention(DateTimeOffset retentionUntil) => RetentionUntil = retentionUntil;
}
