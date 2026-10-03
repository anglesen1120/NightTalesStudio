using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.Sources.Commands.RetrySource;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources/{sourceId}/retry</c> (02.5.1 plan §4 UC5): put a failed url source
/// back to <c>pending</c> with a new fetch task.
/// </summary>
/// <remarks>
/// [DECISION] The audit action is <c>episode.source_retried</c>, not a reuse of <c>episode.source_added</c> with a
/// flag as the task text proposed: the architecture rule <c>Audited_ActionsAreUnique</c> forbids two commands sharing
/// an action. The key is a new D13 entry.
/// </remarks>
/// <param name="EpisodeId">The route value.</param>
/// <param name="SourceId">The failed source to retry.</param>
[Audited("episode.source_retried", Category.Episodes, Visibility.User)]
public sealed record RetrySourceCommand(Guid EpisodeId, Guid SourceId) : ICommand<ErrorOr<SourceDto>>, IAuditData
{
    /// <summary>The row's projection: the retried source; no URL, no note (D13).</summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(null, "story_source", SourceId, AuditData.NoSummary);
}
