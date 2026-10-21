using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.SeriesBible.Commands.DismissBibleProposal;

/// <summary>
/// <c>POST /api/v1/series/{id}/bible/proposal/dismiss</c> (02.6.3-T2, plan §4 U3): the owner throws the pending
/// proposal away without touching the bible's content.
/// </summary>
/// <remarks>
/// <para>
/// No <c>If-Match</c> (D9 does not list this route, and a stale client cannot damage anything: dismissing twice is
/// answered 409 by the second call rather than 412) and no <c>Idempotency-Key</c> — the route spends nothing and
/// writes no version.
/// </para>
/// <para>
/// The audit row's <c>data</c> is the shape the plan names for this action: the series, how many ops were dropped
/// and the episode they came from — never an op's text (D13/D19). The counts are read from the row the handler
/// cleared, so the command stores them in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects them.
/// </para>
/// </remarks>
/// <param name="SeriesId">The series whose pending proposal is dismissed, from the route.</param>
[Audited("series.bible_proposal_dismissed", Category.Episodes, Visibility.User)]
public sealed record DismissBibleProposalCommand(Guid SeriesId) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>The facts of the proposal this command cleared; stays <see langword="null"/> for a failure.</summary>
    [JsonIgnore]
    public DismissBibleProposalOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the series is the entity; the count and the episode id only (D13/D19).</summary>
    /// <returns>The projection, or <see langword="null"/> when no proposal was cleared.</returns>
    public AuditData? ToAuditData()
    {
        if (Outcome is null)
        {
            return null;
        }

        var summary = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["seriesId"] = SeriesId.ToString("D", CultureInfo.InvariantCulture),
            ["ops"] = Outcome.Ops,
        };

        // The episode is named while the row still records one (plan §4 U3 leaves a lost column dismissable), so
        // the key is omitted rather than written as a null the summary's value type cannot express.
        if (Outcome.ProposedFromEpisodeId is { } proposedFromEpisodeId)
        {
            summary["proposedFromEpisodeId"] = proposedFromEpisodeId.ToString("D", CultureInfo.InvariantCulture);
        }

        return new AuditData(null, "series", SeriesId, summary);
    }
}

/// <summary>The audit-relevant facts of a dismissed proposal (D13: a series, a count and an id, no content).</summary>
/// <param name="Ops">How many ops the cleared proposal carried.</param>
/// <param name="ProposedFromEpisodeId">The episode they were computed from, when the row recorded one.</param>
public sealed record DismissBibleProposalOutcome(int Ops, Guid? ProposedFromEpisodeId);
