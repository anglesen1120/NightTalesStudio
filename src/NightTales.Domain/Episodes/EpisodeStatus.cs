namespace NightTales.Domain.Episodes;

/// <summary>
/// Lifecycle state of an episode (00 overview §7, 09 README D16), stored as snake_case text:
/// <c>draft</c>, <c>story_generating</c>, <c>story_review</c>, <c>story_approved</c>,
/// <c>rendering</c>, <c>video_review</c>, <c>ready</c>, <c>scheduled</c>, <c>published</c>,
/// <c>failed</c>, <c>cancelled</c>.
/// </summary>
public enum EpisodeStatus
{
    Draft,
    StoryGenerating,
    StoryReview,
    StoryApproved,
    Rendering,
    VideoReview,
    Ready,
    Scheduled,
    Published,
    Failed,
    Cancelled,
}
