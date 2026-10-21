using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Features.SeriesBible.Commands.UpdateBible;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.SeriesBible.Commands.AcceptBibleProposal;

/// <summary>
/// <c>POST /api/v1/series/{id}/bible/proposal/accept</c> (02.6.3-T2, plan §4 U4): the owner resolves the pending
/// proposal by applying its ops to the bible as it stands now.
/// </summary>
/// <remarks>
/// <para>
/// No <c>If-Match</c> and no <c>Idempotency-Key</c>: the ops are append-only, so an accept cannot conflict with a
/// manual save (F27), and the second call answers 409 <c>bible_proposal_not_pending</c> because the first one
/// cleared the proposal — a repeat is a conflict, not a silent no-op, exactly as D9 asks of a route without a tag.
/// </para>
/// <para>
/// The audit row's <c>data</c> (the series, the two versions, the applied/skipped counts and the source episode,
/// never an op's text, D13/D19) is known only after the handler ran, so the handler stores it in
/// <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it. The keys are the ones the plan names for this
/// action (plan §7.1), which is also what the autopilot auto-accept of the job writes, so one action has one shape
/// whatever wrote it.
/// </para>
/// </remarks>
/// <param name="SeriesId">The series whose pending proposal is accepted, from the route.</param>
[Audited("series.bible_proposal_accepted", Category.Episodes, Visibility.User)]
public sealed record AcceptBibleProposalCommand(Guid SeriesId) : ICommand<ErrorOr<UpdateBibleResult>>, IAuditData
{
    /// <summary>The counts the handler produced; stays <see langword="null"/> for a failure.</summary>
    [JsonIgnore]
    public AcceptBibleProposalOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the series is the entity; ids, versions and counts only (D13/D19).</summary>
    /// <returns>The projection, or <see langword="null"/> when no proposal was applied.</returns>
    public AuditData? ToAuditData() =>
        Outcome is null
            ? null
            : new AuditData(
                null,
                "series",
                SeriesId,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["seriesId"] = SeriesId.ToString("D", CultureInfo.InvariantCulture),
                    ["fromVersion"] = Outcome.FromVersion,
                    ["toVersion"] = Outcome.ToVersion,
                    ["applied"] = Outcome.Applied,
                    ["skipped"] = Outcome.Skipped,
                    ["proposedFromEpisodeId"] = Outcome.ProposedFromEpisodeId.ToString("D", CultureInfo.InvariantCulture),
                });
}

/// <summary>The audit-relevant facts of an accepted proposal (D13: ids, versions and counts, no content).</summary>
/// <param name="FromVersion">The content version the accept applied onto.</param>
/// <param name="ToVersion">The version it produced; always <paramref name="FromVersion"/> + 1.</param>
/// <param name="Applied">How many ops changed the content.</param>
/// <param name="Skipped">How many added nothing: a duplicate, a full array, a summary already stored, or an op the accept dropped.</param>
/// <param name="ProposedFromEpisodeId">The episode the ops were computed from (<c>proposed_from_episode_id</c>).</param>
public sealed record AcceptBibleProposalOutcome(
    int FromVersion,
    int ToVersion,
    int Applied,
    int Skipped,
    Guid ProposedFromEpisodeId);
