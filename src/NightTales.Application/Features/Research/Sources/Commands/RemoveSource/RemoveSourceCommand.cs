using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.Sources.Commands.RemoveSource;

/// <summary>
/// <c>DELETE /api/v1/episodes/{episodeId}/sources/{sourceId}</c> (02.5.1 plan §4 UC4): remove one source of the
/// caller's episode; the <c>fetch_tasks</c> foreign key cascade deletes its queued or leased task.
/// </summary>
/// <param name="EpisodeId">The route value.</param>
/// <param name="SourceId">The source to remove.</param>
[Audited("episode.source_removed", Category.Episodes, Visibility.User)]
public sealed record RemoveSourceCommand(Guid EpisodeId, Guid SourceId) : ICommand<ErrorOr<Deleted>>, IAuditData
{
    /// <summary>The row's projection: the removed source; no URL, no note (D13).</summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(null, "story_source", SourceId, AuditData.NoSummary);
}
