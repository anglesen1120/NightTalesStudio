using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories.Commands.EditStory;

/// <summary>
/// <c>PUT /api/v1/episodes/{id}/story</c> (02.3.1-T1): the owner's full edited <c>story.v1</c> becomes a new
/// <c>edited</c> version under the story ETag. The route is naturally idempotent: an unchanged story is a no-op and a
/// replay with the old ETag is a 412, so the route takes no <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// The audit row is written only when a version was saved; its <c>data</c> (ids and the version number, never text, 02 D19)
/// is known after the handler ran, so the handler stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it.
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="Content">The full edited draft; the backend recomputes every derived field.</param>
/// <param name="IfMatch">The <c>If-Match</c> header value: the story ETag the client read.</param>
[Audited("episode.story_version_saved", Category.Episodes, Visibility.User)]
public sealed record EditStoryCommand(Guid EpisodeId, StoryContentV1 Content, string IfMatch)
    : ICommand<ErrorOr<StoryVersionDto>>, IAuditData
{
    /// <summary>The version the handler saved; stays <see langword="null"/> for a no-op or a failure.</summary>
    [JsonIgnore]
    public EditStoryOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the episode is the entity; ids and numbers only (02 D19).</summary>
    /// <returns>The projection, or <see langword="null"/> when no version was saved.</returns>
    public AuditData? ToAuditData() =>
        Outcome is null
            ? null
            : new AuditData(
                null,
                "episode",
                EpisodeId,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["episodeId"] = EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                    ["versionId"] = Outcome.VersionId.ToString("D", CultureInfo.InvariantCulture),
                    ["version"] = Outcome.Version,
                });
}

/// <summary>The audit-relevant facts of a saved edit.</summary>
/// <param name="VersionId">The new current version.</param>
/// <param name="Version">Its 1-based number.</param>
public sealed record EditStoryOutcome(Guid VersionId, int Version);
