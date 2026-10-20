using ErrorOr;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The content gate of the story gate (02.8.2-T2): check 7 (<c>moderation_gate</c>) over the final narration,
/// the metadata and the expanded scene prompts, with per-category thresholds instead of the human path's D12
/// blocking list.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, no second verdict.</b> <see cref="AutopilotModerationGate"/> is the only producer of the
/// check's outcome; the evaluator, the run log and <c>gate_results</c> all read the same
/// <see cref="ContentGateVerdict"/>, so the threshold a verdict was decided by is reproducible from the stored
/// run row (plan §3.3).
/// </para>
/// <para>
/// <b>Nothing here throws for an outage.</b> The moderator port fails with
/// <see cref="Abstractions.Moderation.ModerationUnavailableException"/>; this gate turns that into a fail-closed
/// verdict (<c>moderator_unavailable</c>) — a story the owner cannot be told about is never approved. Only the
/// caller's own cancellation propagates.
/// </para>
/// <para>
/// <b>The caller opens the moderation scope.</b> The moderator records its zero-cost <c>usage</c> row
/// (<c>operation = moderation</c>) from <see cref="Abstractions.Moderation.ModerationScope"/>, and the request
/// carries ids only — no run context — so the caller that holds the run MUST push the scope around this call
/// (the story gate evaluator does it with the run it already loads). Outside a scope the call is metered as
/// "not metered" and the usage row is never written.
/// </para>
/// </remarks>
public interface IAutopilotContentGate
{
    /// <summary>Checks the run's content against the profile's moderation thresholds.</summary>
    /// <param name="req">The content, its D22 segments and the resolved profile.</param>
    /// <param name="ct">Cancels the check; the only exception the caller must expect.</param>
    /// <returns>
    /// The verdict, or an error when the gate itself could not run. An outage of the moderator is not an error:
    /// it is a flagged verdict with <see cref="ContentGateVerdict.UnavailableDetail"/> set (fail closed).
    /// </returns>
    ValueTask<ErrorOr<ContentGateVerdict>> CheckAsync(AutopilotContentGateRequest req, CancellationToken ct);
}
