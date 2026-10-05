namespace NightTales.Application.Features.Stories.ReadModels;

/// <summary>Bounded history metadata; no content or audit document is loaded.</summary>
/// <param name="VersionId">The immutable version id.</param>
/// <param name="Version">The episode-local version number.</param>
/// <param name="Source">The source's snake_case wire value.</param>
/// <param name="CreatedAt">When the version was saved.</param>
/// <param name="WordCount">The stored word count.</param>
/// <param name="Etag">The D9 strong story ETag.</param>
/// <param name="SimilarityScore">The stored similarity score, if measured.</param>
/// <param name="Approved">Whether the episode's approval pointer names this version.</param>
/// <param name="IsCurrent">Whether the episode's current pointer names this version.</param>
public sealed record StoryVersionSummaryDto(
    Guid VersionId, int Version, string Source, DateTimeOffset CreatedAt, int WordCount,
    string Etag, decimal? SimilarityScore, bool Approved, bool IsCurrent);
