using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The result of one story-gate evaluation (02.8.2 plan §4 UC1, §4 UC5/UC7): the Domain verdict, whether this
/// evaluation actually approved the story, and why it stopped when it did not.
/// </summary>
/// <param name="Result">The Domain verdict of the 15 checks, as it was stored in <c>gate_results</c>.</param>
/// <param name="AutoApproved">
/// True when the approval went through as an autopilot action — the live pass that
/// <see cref="AutopilotApprovalService.ApproveStoryAsync"/> sent and the domain accepted, or the idempotent
/// replay of an approval of the same version. Always <see langword="false"/> in a dry run.
/// </param>
/// <param name="Reason">
/// Why the gate stopped, or <see langword="null"/> for a live pass that approved. A pass that stopped is
/// <see cref="GateStopReason.OwnerApprovalRequired"/> (the approve command was refused).
/// </param>
public sealed record StoryGateOutcome(StoryGateResult Result, bool AutoApproved, GateStopReason? Reason);
