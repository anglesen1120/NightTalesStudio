namespace NightTales.Domain.Usage;

/// <summary>
/// Metered operation of a usage record (00 overview §6 <c>usage_records.operation</c> plus the
/// 02 CR-4 values). Stored as snake_case text with a <c>CHECK</c> constraint.
/// </summary>
public enum UsageOperation
{
    Story,
    StoryReview,
    StoryRevision,
    FactSheet,
    Image,
    Tts,
    Render,
    BibleProposal,
    Idea,
    SourceFind,
    Moderation,
}
