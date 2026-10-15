namespace NightTales.Domain.Rendering;

/// <summary>
/// The lifecycle of one render attempt (BD5; 00 §7 <c>render_jobs.status</c>), stored as the snake_case text of
/// <c>ck_render_jobs_status</c> (09 README D8). Three states are terminal: <see cref="Succeeded"/>,
/// <see cref="Failed"/> and <see cref="Cancelled"/>; <c>cancelled</c> is a state and never an error response
/// (05 D5, 09 §5.4).
/// </summary>
public enum RenderJobStatus
{
    /// <summary>The row the render command wrote: no node has it yet (<c>SubmitRenderJob</c> owns the next step).</summary>
    Queued,

    /// <summary>An adapter accepted the task for a node; the poller is watching it.</summary>
    Submitted,

    /// <summary>The adapter reported progress for a submitted task.</summary>
    Running,

    /// <summary>The output is being downloaded, verified and stored (09.6.3-T2); no poll touches the row meanwhile.</summary>
    Ingesting,

    /// <summary>Terminal: the episode carries a final video and is in <c>video_review</c>.</summary>
    Succeeded,

    /// <summary>Terminal: the attempt ended with an <c>error_code</c> of the 05 D5 outcome list.</summary>
    Failed,

    /// <summary>Terminal: the creator cancelled, or the adapter reported a terminal <c>cancelled</c> (WT51).</summary>
    Cancelled,
}
