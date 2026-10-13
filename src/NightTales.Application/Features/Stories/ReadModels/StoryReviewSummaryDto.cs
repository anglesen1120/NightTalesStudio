using System.Text.Json;

namespace NightTales.Application.Features.Stories.ReadModels;

/// <summary>The stored model-review summary; no review is recomputed on reads.</summary>
/// <param name="Scores">The stored scores object verbatim, or null.</param>
/// <param name="IssueCounts">Counts of the model review's issues by severity.</param>
/// <param name="HasDrift">Whether the audit records a revision-drift advisory on this version (02.6.2-T1); a missing or malformed block fails safe to false.</param>
/// <param name="Stale">Whether the review is stale; missing flags fail safe to true.</param>
public sealed record StoryReviewSummaryDto(JsonElement? Scores, StoryIssueCountsDto IssueCounts, bool HasDrift, bool Stale);

/// <summary>Counts from model_review.issues, not from local quality issues.</summary>
/// <param name="Blocking">The blocking issue count.</param>
/// <param name="Advisory">The advisory issue count.</param>
public sealed record StoryIssueCountsDto(int Blocking, int Advisory);
