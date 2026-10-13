using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.Commands.UpdateFactSheet;

/// <summary>
/// <c>PUT /api/v1/episodes/{id}/fact-sheet</c> (02.5.4-T1, plan §4.2): the owner's full fact list becomes a new
/// <c>draft</c> version under the <c>fs-</c> ETag of the version it was read from. A kept fact keeps its id
/// (F12), a new fact gets the next id after the episode's watermark, and the previous version is never
/// touched (D9).
/// </summary>
/// <remarks>
/// The route is naturally idempotent: an unchanged body still saves a new version, and a replay with the old
/// ETag is a 412, so the route takes no <c>Idempotency-Key</c> — exactly as <c>PUT /episodes/{id}/story</c>
/// does. The audit row's <c>data</c> (ids and the version number, never a statement, 02 D19) is known only
/// after the handler ran, so the handler stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/>
/// projects it.
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="IfMatch">The <c>If-Match</c> header value: the <c>fs-</c> ETag the client read.</param>
/// <param name="Content">The full fact list of the next version; every derived field is recomputed.</param>
[Audited("episode.fact_sheet_saved", Category.Episodes, Visibility.User)]
public sealed record UpdateFactSheetCommand(Guid EpisodeId, string IfMatch, FactSheetContentDto Content)
    : ICommand<ErrorOr<FactSheetSummaryDto>>, IAuditData
{
    /// <summary>The version the handler saved; stays <see langword="null"/> for a failure.</summary>
    [JsonIgnore]
    public FactSheetSavedOutcome? Outcome { get; set; }

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
                    ["episode_id"] = EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                    ["fact_sheet_id"] = Outcome.FactSheetId.ToString("D", CultureInfo.InvariantCulture),
                    ["version"] = Outcome.Version,
                });
}

/// <summary>The audit-relevant facts of a saved edit.</summary>
/// <param name="FactSheetId">The new draft version.</param>
/// <param name="Version">Its 1-based number.</param>
public sealed record FactSheetSavedOutcome(Guid FactSheetId, int Version);
