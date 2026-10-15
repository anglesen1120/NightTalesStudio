namespace NightTales.Application.Features.Publishing.Queries.GetYouTubeEta;

/// <summary>
/// The YouTube upload quota at this moment: <paramref name="Available"/> units left today, and
/// <paramref name="NextSlotAt"/> the first instant a unit is available again (null while units remain).
/// </summary>
public sealed record YouTubeEtaDto(int Available, DateTimeOffset? NextSlotAt);
