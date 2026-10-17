using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.Commands.AddSourceCandidate;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources/candidates/{candidateId}/add</c> (02.7.2 T2, plan §4): turn one
/// proposed candidate of the episode's last find run into a normal source.
/// </summary>
/// <remarks>
/// <para>
/// The add runs the same write path as <c>POST /sources</c> — <c>ISourceAdder</c>, never a nested
/// <c>AddSourceCommand</c> through the mediator, which would commit a second unit of work and a second audit row
/// (02.7.2-T2 R7). A failure of that path (409 <c>source_limit_reached</c>, 429
/// <c>source_fetch_limit_reached</c>, 409 <c>source_duplicate</c>, 400 <c>source_url_invalid</c>, 400
/// <c>source_domain_not_allowed</c>, 409 <c>episode_invalid_state</c>) leaves the candidate <c>proposed</c>.
/// </para>
/// <para>
/// The audit row's <c>data</c> is the episode, the candidate and the created source id — never the candidate's URL
/// or title (D13/D19, 02.7.2-T2 R6/R13).
/// </para>
/// </remarks>
/// <param name="EpisodeId">The route's episode; the candidate must belong to it and to the caller.</param>
/// <param name="CandidateId">The proposed candidate to add.</param>
[Audited("episode.source_candidate_added", Category.Episodes, Visibility.User)]
public sealed record AddSourceCandidateCommand(Guid EpisodeId, Guid CandidateId)
    : ICommand<ErrorOr<SourceDto>>, IAuditDataFromResponse<ErrorOr<SourceDto>>
{
    /// <summary>
    /// The projection without a response (D13: ids only): the episode is the entity and the candidate is the one
    /// fact the command itself knows. <c>AuditBehavior</c> uses <see cref="ToAuditData(ErrorOr{SourceDto})"/>
    /// instead, once the handler has produced the source.
    /// </summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(
        null,
        "episode",
        EpisodeId,
        new Dictionary<string, object>(StringComparer.Ordinal) { ["candidateId"] = CandidateId });

    /// <summary>
    /// The row's projection: the candidate that was added and the source it became — no URL, no title (R6/R13).
    /// </summary>
    /// <param name="response">The created source.</param>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData? ToAuditData(ErrorOr<SourceDto> response) => response.IsError
        ? null
        : new AuditData(
            null,
            "episode",
            EpisodeId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["candidateId"] = CandidateId,
                ["sourceId"] = response.Value.Id,
            });
}
