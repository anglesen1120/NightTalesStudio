using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.VideoReview.Commands.RejectVideo;

/// <summary>
/// What a rejection answered (09.7 plan §5): the episode it moved, the status it left the episode in, and the
/// target that decided it.
/// </summary>
/// <remarks>
/// <para>
/// The target is part of the answer because the route's status code depends on it — <c>story</c> is a 200 and
/// <c>re_render</c> a 202 with no <c>Location</c>, since the render job does not exist yet (WT3) — and because
/// the value must survive the idempotency replay: a repeated <c>Idempotency-Key</c> returns this stored
/// record, so the second answer is the first one, status code included.
/// </para>
/// <para>
/// <see cref="Status"/> is the snake_case wire name of the episode status (D14), so the endpoint puts it into
/// the response body verbatim.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode the rejection moved.</param>
/// <param name="Status">Its status after the rejection: <c>story_review</c> or <c>story_approved</c>.</param>
/// <param name="Target">The target the caller sent.</param>
public sealed record VideoReviewResult(Guid EpisodeId, string Status, RejectTarget? Target);
