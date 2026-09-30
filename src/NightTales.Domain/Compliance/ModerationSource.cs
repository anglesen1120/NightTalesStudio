namespace NightTales.Domain.Compliance;

/// <summary>
/// Component that raised a moderation event (00 overview §6 <c>moderation_events.source</c>).
/// Stored as snake_case text, so the OpenAI sources are <c>openai_image</c> and <c>openai_text</c>.
/// </summary>
public enum ModerationSource
{
    OpenaiImage,
    OpenaiText,
    Internal,
}
