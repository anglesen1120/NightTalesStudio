using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Stories.Commands.ReopenStory;

/// <summary>Unlocks the approved story for editing under its story ETag.</summary>
/// <param name="EpisodeId">The owner-scoped episode.</param>
/// <param name="IfMatch">The approved story version's If-Match header.</param>
[Audited("episode.story_reopened", Category.Episodes, Visibility.User)]
public sealed record ReopenStoryCommand(Guid EpisodeId, string IfMatch)
    : ICommand<ErrorOr<EpisodeStoryStateDto>>, IAuditData
{
    /// <summary>The approved version unlocked by a successful transition; null on failure.</summary>
    [JsonIgnore]
    public Guid? ReopenedStoryVersionId { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData() =>
        ReopenedStoryVersionId is not { } versionId
            ? null
            : new AuditData(
                null,
                "episode",
                EpisodeId,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["episodeId"] = EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                    ["versionId"] = versionId.ToString("D", CultureInfo.InvariantCulture),
                });
}
