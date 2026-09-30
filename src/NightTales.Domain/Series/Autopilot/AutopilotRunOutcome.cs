namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// Terminal or waiting outcome of an autopilot run (00 overview §6 <c>autopilot_runs.outcome</c>),
/// written by the run state machine when it stops (02.8.3-T1). Snake_case text:
/// <c>published</c>, <c>published_private_locked</c>, <c>waiting_owner</c>, <c>gate_failed</c>,
/// <c>cancelled_by_owner</c>, <c>awaiting_authorization</c>, <c>paused</c>, <c>failed</c>.
/// </summary>
public enum AutopilotRunOutcome
{
    Published,
    PublishedPrivateLocked,
    WaitingOwner,
    GateFailed,
    CancelledByOwner,
    AwaitingAuthorization,
    Paused,
    Failed,
}
