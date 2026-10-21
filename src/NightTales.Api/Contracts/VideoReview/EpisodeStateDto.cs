using NightTales.Application.Common;

namespace NightTales.Api.Contracts.VideoReview;

/// <summary>
/// The body of both video-review routes (09.7.1 plan §Design [INFERENCE]: 00 §8 names the routes only): the
/// episode's new wire status and the strong episode ETag the endpoint read <em>after</em> the commit.
/// </summary>
/// <remarks>
/// The value is built in the endpoint once <see cref="NightTales.Api.Http.PostCommitETagReader"/> has read the
/// episode's current <c>xmin</c>, so the <c>etag</c> member and the <c>ETag</c> header are the same strong
/// <c>"ep-&lt;xmin&gt;"</c> tag (09 README D15); <see cref="IHasETag"/> is what makes the shared result helpers
/// write the header from this value.
/// </remarks>
/// <param name="Id">The episode that changed state.</param>
/// <param name="Status">The new wire status, for example <c>ready</c> or <c>story_review</c> (09 README D14).</param>
/// <param name="Etag">The quoted, strong episode ETag, for example <c>"ep-7"</c>.</param>
public sealed record EpisodeStateDto(Guid Id, string Status, string Etag) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}
