using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The result of one video-gate evaluation (02.8.2 plan §4 UC3, §4 UC5): the Domain verdict, whether this
/// evaluation actually approved the video, and why it stopped when it did not.
/// </summary>
/// <param name="Result">The Domain verdict of the 10 checks, as it was stored in <c>gate_results</c>.</param>
/// <param name="AutoApproved">
/// True when <c>ApproveVideoCommand</c> was sent and accepted — or the episode was already <c>ready</c> past
/// the same review. Always <see langword="false"/> in a dry run.
/// </param>
/// <param name="Reason">
/// Why the gate stopped, or <see langword="null"/> for a live pass that approved. There is no hold on this
/// half, so the failing value is <see cref="GateStopReason.FailedChecks"/>.
/// </param>
public sealed record VideoGateOutcome(VideoGateResult Result, bool AutoApproved, GateStopReason? Reason);
