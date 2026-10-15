namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// What one streamed render output cost, as the download accepted it (09 README BD6): the byte count and
/// the SHA-256 of exactly the bytes written to the caller's stream, so the ingest (09.6.3-T2) can compare
/// both with the adapter's own report without reading the output back.
/// </summary>
/// <param name="Bytes">The number of bytes written to the destination stream.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of those bytes (64 characters).</param>
/// <param name="ContentType">The media type the node served (<c>video/mp4</c> or <c>application/x-subrip</c>).</param>
public sealed record DownloadResult(long Bytes, string Sha256, string ContentType);
