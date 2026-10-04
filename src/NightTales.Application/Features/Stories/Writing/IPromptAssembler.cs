using ErrorOr;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>
/// Builds the two strings of a writer call (02 README D5/D6): a static system prompt and one JSON data
/// object. Stateless and pure: no I/O and no clock.
/// </summary>
public interface IPromptAssembler
{
    /// <summary>Assembles the prompt of one writer call.</summary>
    /// <param name="req">The write request; its brief, bible, facts and avoid-plots are untrusted data.</param>
    /// <param name="ctx">The catalog coordinates of the call.</param>
    /// <returns>The system prompt, the data object JSON, the prompt version and the cache key.</returns>
    AssembledPrompt Assemble(StoryWriteRequest req, AssemblerContext ctx);

    /// <summary>
    /// Assembles the review call (02.6.1-T1): the static review system prompt for <c>(language, tier)</c> and the one JSON
    /// data object that carries the draft, the facts, the bible and the ear-rule questions.
    /// </summary>
    /// <param name="req">The review request.</param>
    /// <returns>The two strings, the composite prompt version and the prompt-cache key; or <c>prompt_too_large</c> when the data object exceeds twice <c>Story:Prompt:MaxDataObjectBytes</c>.</returns>
    ErrorOr<AssembledPrompt> Review(Review.StoryReviewRequest req);
}
