namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// The live position of one autopilot run (02.8.3 plan §3.5): the <c>autopilot_runs.state</c> column, stored as
/// its snake_case wire value (<c>planned</c>, never <c>Planned</c>) and guarded by the table's state
/// <c>CHECK</c>, so the C# member, the column and the CHECK cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// The members are declared in the order the run walks the machine, followed by the two blocked states
/// (<see cref="WaitingOwner"/>, <see cref="Paused"/>) and the five terminal states. The legal moves between them
/// are <see cref="AutopilotRunStateMachine.CanTransition"/>; a move that the plan's transition table does not
/// contain never happens, because <see cref="AutopilotRun"/> is the only writer of this column and its methods
/// ask the machine first.
/// </para>
/// <para>
/// Wire values, in this order: <c>planned</c>, <c>awaiting_authorization</c>, <c>idea_picked</c>,
/// <c>researching</c>, <c>story_generating</c>, <c>story_gate</c>, <c>waiting_owner</c>, <c>rendering</c>,
/// <c>video_gate</c>, <c>publication_created</c>, <c>veto_window</c>, <c>scheduled</c>, <c>published</c>,
/// <c>published_private_locked</c>, <c>gate_failed</c>, <c>cancelled_by_owner</c>, <c>paused</c>, <c>failed</c>.
/// </para>
/// </remarks>
public enum AutopilotRunState
{
    /// <summary>
    /// The slot has a row and nothing is dispatched yet (row <c>planned → idea_picked</c>; the tick moves it once
    /// <c>now ≥ start_at</c>). Also the state a paused run resumes to when its row carries no step yet.
    /// </summary>
    Planned,

    /// <summary>
    /// The series has no valid authorization, so production is blocked without pausing the series (row
    /// <c>planned → awaiting_authorization</c>, cleared back to <see cref="Planned"/> by
    /// <c>AutopilotAuthorizationAccepted</c>). A blocked, not a terminal, state: its <c>outcome</c> is written and
    /// <c>finished_at</c> is not.
    /// </summary>
    AwaitingAuthorization,

    /// <summary>
    /// The slot's idea was picked and its episode created (row <c>planned → idea_picked</c>).
    /// </summary>
    IdeaPicked,

    /// <summary>
    /// A true-crime run is finding sources or drafting the fact sheet (row <c>idea_picked → researching</c>, steps
    /// <c>find_sources</c> and <c>fact_sheet</c>). Fiction never enters this state.
    /// </summary>
    Researching,

    /// <summary>
    /// A story generation has been dispatched (row <c>planned/idea_picked → story_generating</c>). Re-entered once
    /// by the one regenerate (row <c>story_generating → story_generating</c> and <c>story_gate → story_generating</c>).
    /// </summary>
    StoryGenerating,

    /// <summary>
    /// The story exists and waits for its gate verdict (row <c>story_generating → story_gate</c>).
    /// </summary>
    StoryGate,

    /// <summary>
    /// The owner is needed: a fact sheet, the sources of a true-crime run, a held story, or a held video (rows
    /// <c>idea_picked → waiting_owner</c>, <c>researching → waiting_owner</c>, <c>story_gate → waiting_owner</c>,
    /// <c>video_gate → waiting_owner</c>). A blocked state: <c>outcome</c> is <c>waiting_owner</c> while the run
    /// sits here and <c>finished_at</c> stays null. <see cref="AutopilotRun.WaitingFor"/> names what is missing.
    /// </summary>
    WaitingOwner,

    /// <summary>
    /// A render has been dispatched (row <c>story_gate → rendering</c> or <c>waiting_owner → rendering</c>), at
    /// most once per run.
    /// </summary>
    Rendering,

    /// <summary>
    /// The render succeeded and the video waits for its gate verdict (row <c>rendering → video_gate</c>).
    /// </summary>
    VideoGate,

    /// <summary>
    /// The publication exists (row <c>video_gate → publication_created</c> or
    /// <c>waiting_owner → publication_created</c>) and the veto notice is being delivered.
    /// </summary>
    PublicationCreated,

    /// <summary>
    /// The veto notice reached the owner and consent is pending (row <c>publication_created → veto_window</c>).
    /// Production is over here, so the next slot's run may start (plan §3.5, the concurrency guard).
    /// </summary>
    VetoWindow,

    /// <summary>
    /// Consent is written and the publication is scheduled on the platform (row <c>veto_window → scheduled</c>).
    /// </summary>
    Scheduled,

    /// <summary>Terminal: the publication is live (row <c>scheduled → published</c>).</summary>
    Published,

    /// <summary>
    /// Terminal: the publication is processed on the channel but stays private because the project is in
    /// pre-audit mode (row <c>scheduled → published_private_locked</c>).
    /// </summary>
    PublishedPrivateLocked,

    /// <summary>
    /// Terminal: a gate failed with no attempt left (rows <c>story_gate → gate_failed</c> and
    /// <c>video_gate → gate_failed</c>).
    /// </summary>
    GateFailed,

    /// <summary>
    /// Terminal: the owner cancelled the veto window or reopened the story (row
    /// <c>veto_window → cancelled_by_owner</c>).
    /// </summary>
    CancelledByOwner,

    /// <summary>
    /// Blocked: a plan §3.6 trigger stopped the row (row <c>any non-terminal → paused</c>) and only the owner
    /// resumes it (row <c>paused → the state its step implies</c>). <c>outcome</c> is <c>paused</c>;
    /// <see cref="AutopilotRun.PauseReason"/> names the trigger.
    /// </summary>
    Paused,

    /// <summary>
    /// Terminal: the row gave up (row <c>any non-terminal → failed</c>: a moderated story, a render failure, a
    /// missed slot, an expired owner wait, an unrecoverable error). The reason is a code for the log, never a
    /// column (plan §3.3).
    /// </summary>
    Failed,
}
