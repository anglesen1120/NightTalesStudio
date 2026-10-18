using ErrorOr;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The one seam 02.8.3-T1's orchestrator calls (02.8.2 plan §4 UC1/UC3): evaluate the story gate of a run, or
/// the video gate of its finished render, from stored data only.
/// </summary>
/// <remarks>
/// <para>
/// <b>No HTTP route, no client error.</b> A gate verdict is not something a client asks for; the orchestrator
/// calls it inside the Worker's tick or on the render-done delivery (plan §4).
/// </para>
/// <para>
/// <b>Tenant scope.</b> Both methods read and write one run's rows, so they must be resolved in a scope pinned
/// to that run's owner (<c>ISystemContext.For</c>) or the reads see nothing (09 README D10). The approval
/// service re-pins its own scope for the commands it sends, so an approval never runs wider than its run.
/// </para>
/// <para>
/// <b>An error is not a verdict.</b> A returned <see cref="ErrorOr{T}"/> failure means the gate could not be
/// evaluated at all — the run or its episode is unreadable, the similarity corpus load failed — and the caller
/// retries; a story that failed a check is a successful call carrying
/// <see cref="StoryGateResult.Pass"/> <c>= false</c> (plan §"Failure handling": no pass on unknown).
/// </para>
/// </remarks>
public interface IAutoApprovalGate
{
    /// <summary>Evaluates the 15 story checks of one run and approves on a live pass.</summary>
    /// <param name="runId">The run whose current story version is evaluated.</param>
    /// <param name="attempt">The story-gate attempt, 1-based (D17 allows two generations per run).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The verdict, or the error that stopped the evaluation.</returns>
    ValueTask<ErrorOr<StoryGateOutcome>> EvaluateStoryAsync(Guid runId, int attempt, CancellationToken ct);

    /// <summary>Evaluates the 10 video checks of one run's finished render and approves on a live pass.</summary>
    /// <param name="runId">The run whose finished render is evaluated.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The verdict, or the error that stopped the evaluation.</returns>
    ValueTask<ErrorOr<VideoGateOutcome>> EvaluateVideoAsync(Guid runId, CancellationToken ct);
}
