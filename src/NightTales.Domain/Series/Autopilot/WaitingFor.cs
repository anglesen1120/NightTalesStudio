namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// Why a run sits in <see cref="AutopilotRunState.WaitingOwner"/> (02.8.3 plan §3.3, column
/// <c>autopilot_runs.waiting_for</c>): stored as its snake_case wire value (<c>fact_sheet</c>, <c>sources</c>,
/// <c>story</c>, <c>video</c>) and guarded by the table's <c>waiting_for</c> <c>CHECK</c>.
/// </summary>
/// <remarks>
/// The column is written by <see cref="AutopilotRun.SetWaitingFor"/> before the row enters
/// <see cref="AutopilotRunState.WaitingOwner"/> and cleared by the aggregate when the row leaves that state, so
/// the "why does this run need me" question is answerable without reading the episode.
/// </remarks>
public enum WaitingFor
{
    /// <summary>A drafted fact sheet waits for the owner's approval (plan §3.5 row <c>researching → waiting_owner</c>).</summary>
    FactSheet,

    /// <summary>
    /// The true-crime run has no source-finder go, so the owner must paste the links (F33, plan §3.5 row
    /// <c>idea_picked → waiting_owner</c>, alert copy <c>autopilot.true_crime.add_sources</c>).
    /// </summary>
    Sources,

    /// <summary>A true-crime story waits for the owner because those gates never auto-approve (row <c>story_gate → waiting_owner</c>).</summary>
    Story,

    /// <summary>The video gate held the render for the owner, or the gate ran in dry-run mode (row <c>video_gate → waiting_owner</c>).</summary>
    Video,
}
