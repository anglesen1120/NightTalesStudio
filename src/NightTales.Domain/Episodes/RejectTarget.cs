namespace NightTales.Domain.Episodes;

/// <summary>
/// Where a rejected video sends the episode (09.7 plan §3, [RULED — README §B10 WT3]): back to the story
/// editor so the owner can fix the story, or on to a re-render of the story that is already approved.
/// </summary>
/// <remarks>
/// <para>
/// The wire values are the snake_case names of the members — <c>story</c> and <c>re_render</c>. The API and
/// the outbox serializer each turn one member into one lowercase string (09 README D14,
/// <c>Infrastructure.Persistence.Outbox.OutboxJson</c>), so a stored <c>video_rejected.v1</c> payload keeps
/// its meaning when a member is renumbered.
/// </para>
/// <para>
/// The rejection itself only writes the successor state; for <see cref="ReRender"/> the render starts later
/// in the Worker's own scope (<c>VideoReRenderHandler</c>), never inside the rejecting transaction.
/// </para>
/// </remarks>
public enum RejectTarget
{
    /// <summary><c>video_review → story_review</c>: the owner edits the story again before the next render.</summary>
    Story,

    /// <summary><c>video_review → story_approved</c>: the Worker renders the already-approved story again (WT3).</summary>
    ReRender,
}
