using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories.Review;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Review;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>
/// The writer step of the story pipeline (02 README D21). It assembles, calls and meters; it does not
/// validate, retry or persist — <c>GenerateStoryJob</c> owns those.
/// </summary>
/// <remarks>
/// <c>ReviewAsync</c> (02.6.1-T1) and <c>ReviseAsync</c> (02.6.2-T1) are the D21 rows 4–5. Each method
/// assembles, calls and meters one step; validation, the single invalid-output retry and persistence stay
/// with <c>GenerateStoryJob</c>.
/// </remarks>
public interface IStoryWriter
{
    /// <summary>Writes one draft.</summary>
    /// <param name="req">The write request; <c>ViolatedRules</c> is set only on the single invalid-output retry.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The mapped outcome of the call; its usage has already been recorded.</returns>
    ValueTask<TextCallResult<StoryContentV1>> WriteAsync(StoryWriteRequest req, CancellationToken ct);

    /// <summary>Reviews one draft (02.6.1-T1): one model call that returns a <c>review.v1</c> document.</summary>
    /// <param name="req">The review request.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The mapped outcome of the call; its usage has already been recorded. The document is not validated.</returns>
    ValueTask<TextCallResult<StoryReviewV1>> ReviewAsync(StoryReviewRequest req, CancellationToken ct);

    /// <summary>Revises one reviewed draft (02.6.2-T1): one model call that returns a corrected <c>story.v1</c> document.</summary>
    /// <param name="req">The revision request; its issues are the blocking objective ones, but the assembler drops any other.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The mapped outcome of the call; its usage has already been recorded. The document is not validated and not retried.</returns>
    ValueTask<TextCallResult<StoryContentV1>> ReviseAsync(StoryRevisionRequest req, CancellationToken ct);
}
