namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// Why one gate evaluation stopped instead of approving (02.8.2 plan §4 UC5–UC7): the reason an
/// <see cref="StoryGateOutcome"/> or a <see cref="VideoGateOutcome"/> carries, and the value
/// <see cref="GateOutcomeRecorder"/> maps to one of <see cref="OwnerActionReasons"/>.
/// </summary>
/// <remarks>
/// The member names are not wire values: the stored verdict keeps <c>hold</c>/<c>pass</c> and the notice keeps
/// its own code, so this enum only has to be unique inside one process. <see cref="None"/> means "the gate did
/// not stop": it is the reason of an outcome that only failed a check, and the callers that notify pass
/// <see langword="null"/> instead of it.
/// </remarks>
public enum GateStopReason
{
    /// <summary>The gate ran but nothing is approved because the check list did not pass.</summary>
    None,

    /// <summary>The gate ran in dry-run mode (02.8.2-T3): the verdict is recorded and nothing is approved.</summary>
    DryRun,

    /// <summary>The true-crime hold of plan §3.1 check 15: held for the owner by design, never a failure.</summary>
    Hold,

    /// <summary>A check did not pass, or could not be evaluated (fail closed).</summary>
    FailedChecks,

    /// <summary>The gate is live and passed, but the approval it asked for was refused; the owner decides.</summary>
    OwnerApprovalRequired,

    /// <summary>The moderator was unreachable, so <c>moderation_gate</c> failed closed.</summary>
    ModeratorUnavailable,
}
