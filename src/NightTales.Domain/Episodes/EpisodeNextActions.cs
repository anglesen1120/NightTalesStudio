namespace NightTales.Domain.Episodes;

/// <summary>
/// One action the episode page may offer for the episode's current state (00 §7, 09 README D16, BD1
/// names the artefact <c>EpisodeNextActions</c>).
/// </summary>
/// <remarks>
/// <para>
/// A hint for the UI, never an authorization: every command re-checks its own transition in the domain
/// (09.5.3 plan §4), so a member here grants nothing. The API publishes these as snake_case strings
/// (<c>generate_story</c>, …) in a <c>nextActions</c> array, exactly as 09 README D14 serializes an
/// enum member.
/// </para>
/// <para>
/// The enum is deliberately <em>not</em> <c>[Flags]</c>: D14 turns one member into one snake_case
/// string, and a combined flags value would become a comma-joined pseudo-name instead.
/// </para>
/// </remarks>
public enum EpisodeNextAction
{
    /// <summary><c>draft → story_generating</c>: start the generation run (02.2.2-T2).</summary>
    GenerateStory,

    /// <summary><c>story_review</c>: open the story editor, which only a review-state story accepts (02.3.1-T1).</summary>
    EditStory,

    /// <summary><c>story_review → story_approved</c>: accept the current version (02.6.1-T1).</summary>
    ApproveStory,

    /// <summary><c>story_review → story_generating</c> (regenerate) and <c>story_approved → story_review</c> (reopen for edits, 00 §7).</summary>
    ReopenStory,

    /// <summary><c>story_approved → rendering</c>: submit a render job (09.6.1-T1).</summary>
    Render,

    /// <summary><c>failed</c>: submit another render job after a render failure (09.6.1-T1).</summary>
    RetryRender,

    /// <summary><c>… → cancelled</c>: the user deletes the episode (09 README D16, <c>DELETE /episodes/{id}</c>).</summary>
    Cancel,

    /// <summary><c>video_review → ready</c>: accept the rendered video (09.7.1-T1).</summary>
    ApproveVideo,

    /// <summary><c>video_review → story_review | story_approved</c>: reject the video, with or without a re-render — the re-render starts later in the Worker (09.7.1-T1, WT3).</summary>
    RejectVideo,

    /// <summary><c>ready → scheduled</c>: open the publication flow for the approved video (06.2.1-T1).</summary>
    Publish,

    /// <summary>The signed video URL is available: <c>GET /episodes/{id}/video-url</c> (09.7.2-T1).</summary>
    Download,
}

/// <summary>
/// The next-actions table of the episode state machine (00 §7, 09 README D16): the actions an episode in
/// a given <see cref="EpisodeStatus"/> allows, in declaration order of <see cref="EpisodeNextAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure and clock-free: no <c>ErrorOr</c>, no database, no <c>DateTimeOffset</c>. The table is the
/// <em>state machine</em> view of 00 §7 — every edge that a user can take from a state, mapped to the
/// action that starts it:
/// </para>
/// <list type="bullet">
/// <item><description><c>draft → story_generating</c> (<see cref="EpisodeNextAction.GenerateStory"/>) and the delete edge of 00 §7's last line.</description></item>
/// <item><description><c>story_review</c>: the story editor (<see cref="EpisodeNextAction.EditStory"/>, 02.3.1-T1 accepts only a review-state story), approve (<see cref="EpisodeNextAction.ApproveStory"/>) and the regenerate/reopen edge (<see cref="EpisodeNextAction.ReopenStory"/>).</description></item>
/// <item><description><c>story_approved</c>: render plus the reopen edge of 00 §7 L341 — the version panel and the approve/reopen dialogs of 10.5.4/10.5.6 offer "reopen to edit" from this state.</description></item>
/// <item><description><c>video_review</c>: approve, reject (with or without a re-render) and the download of the cut under review (10.6.2-T1 gates its download link on <see cref="EpisodeNextAction.Download"/>).</description></item>
/// <item><description><c>ready → scheduled → published</c>: the publication flow (<see cref="EpisodeNextAction.Publish"/>, 10.7.2's create affordance) with the download still available.</description></item>
/// <item><description><c>failed</c>: retry the render (<see cref="EpisodeNextAction.RetryRender"/>) plus the delete edge of 00 §7 (<see cref="EpisodeNextAction.Cancel"/>).</description></item>
/// <item><description><c>published</c> and <c>cancelled</c> are terminal: no action.</description></item>
/// </list>
/// <para>
/// <see cref="EpisodeStatus.StoryGenerating"/> and <see cref="EpisodeStatus.Rendering"/> keep the state
/// machine's delete edge (00 §7: "any non-terminal → … cancelled") even though <c>Episode.Cancel</c>
/// refuses an immediate delete while the generator or the renderer owns the row (09 README D16): the
/// hint names the intended way out, the 409 the command answers is what the generator's own cancel path
/// clears. The command stays the authority.
/// </para>
/// </remarks>
public static class EpisodeNextActions
{
    private static readonly EpisodeNextAction[] DraftActions =
        [EpisodeNextAction.GenerateStory, EpisodeNextAction.Cancel];

    private static readonly EpisodeNextAction[] StoryGeneratingActions =
        [EpisodeNextAction.Cancel];

    private static readonly EpisodeNextAction[] StoryReviewActions =
    [
        EpisodeNextAction.EditStory,
        EpisodeNextAction.ApproveStory,
        EpisodeNextAction.ReopenStory,
        EpisodeNextAction.Cancel,
    ];

    private static readonly EpisodeNextAction[] StoryApprovedActions =
        [EpisodeNextAction.Render, EpisodeNextAction.ReopenStory, EpisodeNextAction.Cancel];

    private static readonly EpisodeNextAction[] RenderingActions =
        [EpisodeNextAction.Cancel];

    private static readonly EpisodeNextAction[] VideoReviewActions =
    [
        EpisodeNextAction.ApproveVideo,
        EpisodeNextAction.RejectVideo,
        EpisodeNextAction.Download,
        EpisodeNextAction.Cancel,
    ];

    private static readonly EpisodeNextAction[] ReadyActions =
        [EpisodeNextAction.Publish, EpisodeNextAction.Download, EpisodeNextAction.Cancel];

    private static readonly EpisodeNextAction[] ScheduledActions =
        [EpisodeNextAction.Publish, EpisodeNextAction.Cancel];

    private static readonly EpisodeNextAction[] FailedActions =
        [EpisodeNextAction.RetryRender, EpisodeNextAction.Cancel];

    /// <summary>The actions <paramref name="status"/> allows, in <see cref="EpisodeNextAction"/> declaration order.</summary>
    /// <remarks>
    /// The returned list is a cached, immutable-by-contract view: the same instance is handed to every
    /// request for a status, so the read path allocates nothing per call. A status the enum does not
    /// define (a member added later and missed here) answers the empty list, never an exception — a new
    /// status hides its actions instead of breaking the episode page.
    /// </remarks>
    /// <param name="status">The episode's lifecycle state.</param>
    /// <returns>The actions the UI may offer; empty for a terminal or unknown state.</returns>
    public static IReadOnlyList<EpisodeNextAction> For(EpisodeStatus status) => status switch
    {
        EpisodeStatus.Draft => DraftActions,
        EpisodeStatus.StoryGenerating => StoryGeneratingActions,
        EpisodeStatus.StoryReview => StoryReviewActions,
        EpisodeStatus.StoryApproved => StoryApprovedActions,
        EpisodeStatus.Rendering => RenderingActions,
        EpisodeStatus.VideoReview => VideoReviewActions,
        EpisodeStatus.Ready => ReadyActions,
        EpisodeStatus.Scheduled => ScheduledActions,
        EpisodeStatus.Failed => FailedActions,
        EpisodeStatus.Published or EpisodeStatus.Cancelled => Array.Empty<EpisodeNextAction>(),
        _ => Array.Empty<EpisodeNextAction>(),
    };
}
