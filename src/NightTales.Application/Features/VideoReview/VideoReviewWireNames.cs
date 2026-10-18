using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.VideoReview;

/// <summary>
/// The wire names of the video-review feature (09 README D14): the rejection target
/// (<c>story</c> | <c>re_render</c>) and the episode status a rejection leaves behind
/// (<c>story_review</c> | <c>story_approved</c>).
/// </summary>
/// <remarks>
/// <para>
/// The outbox serializer writes an enum with the snake_case policy itself (see
/// <c>NightTales.Domain.Episodes.RejectTarget</c>), so nothing here is needed for the stored payload. These
/// two mappings are for the places that speak to a client or to the activity feed: the reject route's body
/// and the rejection's audit row, neither of which may carry a CLR member name (<c>ReRender</c>).
/// </para>
/// <para>
/// Both live here, once, so the answer a caller receives and the row the platform records for one rejection
/// can never disagree about what it was.
/// </para>
/// </remarks>
public static class VideoReviewWireNames
{
    /// <summary>The wire value of <paramref name="target"/>: <c>story</c> or <c>re_render</c>.</summary>
    /// <param name="target">The rejection target; an undefined value is a programming error.</param>
    /// <returns>The snake_case wire name (D14).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not a defined member.</exception>
    public static string ToWireName(this RejectTarget target) => target switch
    {
        RejectTarget.Story => "story",
        RejectTarget.ReRender => "re_render",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown rejection target."),
    };

    /// <summary>
    /// The status a rejection sends the episode to (09 README §B10 WT3): <c>story_review</c> to edit the story
    /// again, <c>story_approved</c> for the re-render the Worker starts.
    /// </summary>
    /// <param name="target">The rejection target; an undefined value is a programming error.</param>
    /// <returns>The snake_case wire name of the successor status (D14).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not a defined member.</exception>
    public static string SuccessorStatus(this RejectTarget target) => target switch
    {
        RejectTarget.Story => "story_review",
        RejectTarget.ReRender => "story_approved",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown rejection target."),
    };
}
