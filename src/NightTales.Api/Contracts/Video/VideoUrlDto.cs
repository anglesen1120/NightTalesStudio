namespace NightTales.Api.Contracts.Video;

/// <summary>
/// The body of <c>GET /api/v1/episodes/{id}/video-url</c> (09.7.2 plan §5): the presigned URL and the
/// instant it stops working.
/// </summary>
/// <remarks>
/// The URL carries a signature, so it is fetched on demand and never stored by a client: the client
/// asks again when it needs to play or download (10.5.2). <c>expiresAt</c> is the same instant the
/// signature was built for, so a client can refresh before it lapses.
/// </remarks>
/// <param name="Url">The presigned <c>GET</c> URL (≤ 15 minutes of validity, WF16).</param>
/// <param name="ExpiresAt">When <paramref name="Url"/> stops working, in UTC.</param>
public sealed record VideoUrlDto(string Url, DateTimeOffset ExpiresAt);
