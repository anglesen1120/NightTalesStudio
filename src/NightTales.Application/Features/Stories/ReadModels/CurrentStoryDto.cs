using System.Text.Json;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Stories.ReadModels;

/// <summary>The stored story read model; never recomputes content or quality checks.</summary>
/// <param name="VersionId">The immutable version id.</param>
/// <param name="Version">The episode-local version number.</param>
/// <param name="Source">The source actor action, snake_case.</param>
/// <param name="Etag">The D9 strong story ETag.</param>
/// <param name="Status">The episode lifecycle state.</param>
/// <param name="Content">The stored story object verbatim, or null when unreadable.</param>
/// <param name="Body">The stored derived body.</param>
/// <param name="Keywords">The stored keywords in story order.</param>
/// <param name="WordCount">The stored derived word count.</param>
/// <param name="QualityHints">Stored local issues, objective first (R7).</param>
/// <param name="AuditReport">The stored audit object verbatim, or null when unreadable.</param>
/// <param name="SimilarityScore">The stored similarity score, if measured.</param>
/// <param name="FactSheetId">The source fact sheet id, if any.</param>
/// <param name="ApprovedStoryVersionId">The episode approval pointer; null when reopened.</param>
/// <param name="Review">The stored model review summary, if present.</param>
/// <param name="Attestation">
/// The true-crime attestation state of this version (02.3.3-T1), or null when the episode is not
/// true-crime-governed: a series whose topic is true crime, or a version that carries a fact sheet id.
/// </param>
public sealed record CurrentStoryDto(
    Guid VersionId,
    int Version,
    string Source,
    string Etag,
    EpisodeStatus Status,
    JsonElement? Content,
    string Body,
    IReadOnlyList<string> Keywords,
    int WordCount,
    IReadOnlyList<QualityHintDto> QualityHints,
    JsonElement? AuditReport,
    decimal? SimilarityScore,
    Guid? FactSheetId,
    Guid? ApprovedStoryVersionId,
    StoryReviewSummaryDto? Review,
    AttestationViewDto? Attestation);
