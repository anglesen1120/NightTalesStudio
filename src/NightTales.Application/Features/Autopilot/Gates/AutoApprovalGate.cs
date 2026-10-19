using ErrorOr;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The one entry point of the gate engine (02.8.2 plan §4 UC1/UC3, §Steps 9): it delegates each half to its own
/// evaluator, so 02.8.3-T1's orchestrator depends on one type instead of two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a facade.</b> <see cref="IAutoApprovalGate"/> carries both methods, and one interface with two methods
/// cannot be implemented by two single-purpose classes: the story and video evaluators stay separate — they read
/// different tables, run different Domain rules and stop for different reasons — and this class is the only place
/// they meet. It holds no state and no logic: every decision belongs to the evaluator behind it.
/// </para>
/// <para>
/// Scoped, like both evaluators and the read model they share.
/// </para>
/// </remarks>
/// <param name="story">The 15-check story evaluator.</param>
/// <param name="video">The 10-check video evaluator.</param>
public sealed class AutoApprovalGate(StoryGateEvaluator story, VideoGateEvaluator video) : IAutoApprovalGate
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(Guid runId, int attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(story);

        return story.EvaluateAsync(runId, attempt, ct);
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(video);

        return video.EvaluateAsync(runId, ct);
    }
}
