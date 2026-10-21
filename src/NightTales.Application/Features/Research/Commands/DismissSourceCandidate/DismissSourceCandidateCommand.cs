using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.Commands.DismissSourceCandidate;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources/candidates/{candidateId}/dismiss</c> (02.7.2 T2, plan §4): throw one
/// proposed candidate of the episode's last find run away; nothing is added to the episode.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent by contract (02.7.2-T2 R8): dismissing a candidate that is already <c>dismissed</c> answers 204 again,
/// an <c>added</c> candidate answers 409 <c>source_candidate_not_proposed</c>, and another user's — or another
/// episode's — candidate answers 404.
/// </para>
/// <para>
/// The audit row is written only when this call changed the row (D13/BD15): <see cref="Changed"/> stays
/// <see langword="false"/> for the repeat, so a no-op dismissal records nothing. The summary is the candidate id —
/// never its URL or title (R6/R13).
/// </para>
/// </remarks>
/// <param name="EpisodeId">The route's episode; the candidate must belong to it and to the caller.</param>
/// <param name="CandidateId">The proposed candidate to dismiss.</param>
[Audited("episode.source_candidate_dismissed", Category.Episodes, Visibility.User)]
public sealed record DismissSourceCandidateCommand(Guid EpisodeId, Guid CandidateId)
    : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>Whether this call moved the candidate from <c>proposed</c> to <c>dismissed</c>.</summary>
    [JsonIgnore]
    public bool Changed { get; set; }

    /// <summary>The row's projection: the episode and the candidate, or nothing for the idempotent repeat.</summary>
    /// <returns>The projection, or <see langword="null"/> when the candidate was already dismissed.</returns>
    public AuditData? ToAuditData() => Changed
        ? new AuditData(
            null,
            "episode",
            EpisodeId,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["candidateId"] = CandidateId })
        : null;
}
