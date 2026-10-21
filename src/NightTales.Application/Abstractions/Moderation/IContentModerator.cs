namespace NightTales.Application.Abstractions.Moderation;

/// <summary>
/// The content moderation port (02 README D12): the free moderation endpoint over the texts of one run stage.
/// A check that cannot complete never passes silently: it throws <see cref="ModerationUnavailableException"/>,
/// which the job maps to <c>story_generation_failed</c> (fail closed).
/// </summary>
public interface IContentModerator
{
    /// <summary>Checks the texts; a flag in any text, or in any chunk of one, flags the result.</summary>
    /// <param name="texts">The texts to check; empty and whitespace-only entries are skipped.</param>
    /// <param name="stage">The stage the check belongs to.</param>
    /// <param name="ct">Cancels the check; the only other exception is <see cref="ModerationUnavailableException"/>.</param>
    /// <returns>The union verdict.</returns>
    /// <exception cref="ModerationUnavailableException">The endpoint failed after the retry budget.</exception>
    ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct);
}
