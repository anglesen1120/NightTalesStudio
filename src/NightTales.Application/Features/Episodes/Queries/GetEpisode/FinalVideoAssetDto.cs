namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// The metadata of the episode's final video asset (09.5.3 plan §5, README §B10 WT53).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No URL.</strong> The only signed media URL comes from <c>GET /episodes/{id}/video-url</c>
/// (09.7.2-T1, TTL 15 min, audited — BD10/WF16): a URL in this block would outlive its signature, would
/// be cached with the page and would leak into logs and <c>Referer</c> headers. The storage key is not
/// carried either (BD16 forbids storage keys in a response a browser holds).
/// </para>
/// <para>
/// The values come from <c>assets</c> — <c>size_bytes</c>, <c>sha256</c>, <c>content_type</c>,
/// <c>duration_sec</c>, <c>retention_until</c> (WT53; the table has no <c>created_at</c> column). The
/// member list is [INFERENCE] (only the artefact name is pinned by BD1).
/// </para>
/// </remarks>
/// <param name="AssetId">The asset's id (<c>episodes.final_video_asset_id</c>).</param>
/// <param name="SizeBytes">The stored size in bytes.</param>
/// <param name="Sha256">The lowercase SHA-256 hex of the stored bytes.</param>
/// <param name="ContentType">The media type, <c>video/mp4</c> for a render output.</param>
/// <param name="DurationSec">The duration in seconds, or null while the probe has not filled it.</param>
/// <param name="RetentionUntil">When retention may delete the asset, or null while unapproved (09.7.1/09.7.3).</param>
public sealed record FinalVideoAssetDto(
    Guid AssetId,
    long SizeBytes,
    string Sha256,
    string ContentType,
    double? DurationSec,
    DateTimeOffset? RetentionUntil);
