using System.Text.Json.Serialization;
using NightTales.Domain.Episodes;

namespace NightTales.Api.Contracts.VideoReview;

/// <summary>
/// The body of <c>POST /api/v1/episodes/{id}/video/reject</c> (09.7.1 plan §Design): why the video was
/// rejected and where the episode goes next.
/// </summary>
/// <remarks>
/// <para>
/// The target is bound as <see cref="RejectTarget"/>, whose wire values are the snake_case names
/// (<c>story</c> and <c>re_render</c>, 09 README D14). An unknown name is a body the endpoint cannot read, so
/// the request answers 400 <c>validation_failed</c> with field <c>target</c> — never 500, and never a bare
/// framework 400 whose code a client cannot match.
/// </para>
/// <para>
/// The member is a required discriminator (<see cref="JsonRequiredAttribute"/>), so a body that omits it is
/// refused the same way, under <c>target</c>, instead of defaulting to <c>story</c> and silently sending the
/// episode to the story editor. The property stays non-nullable so the published contract keeps saying that
/// <c>reason</c> and <c>target</c> are required.
/// </para>
/// <para>
/// The reason is user text: it is stored on the episode and audited as a present/absent flag only, never as
/// text.
/// </para>
/// </remarks>
/// <param name="Reason">The owner's reason, 1–500 characters after trimming; a blank one is invalid.</param>
/// <param name="Target">Where the rejection sends the episode (WT3); a body without it is refused.</param>
public sealed record RejectVideoRequest(string Reason, [property: JsonRequired] RejectTarget Target);
