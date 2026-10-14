using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.Commands.ApproveFactSheet;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/fact-sheet/approve</c> (02.5.4-T1, plan §4.3): locks the draft the client read
/// as the episode's approved version, under its <c>fs-</c> ETag and only once the source-diversity rule (F13)
/// holds. The story is not touched: staleness is detected at story approve through
/// <c>story.fact_sheet_id</c> (F12, 02.5.5-T1).
/// </summary>
/// <remarks>
/// The audit row's <c>data</c> (ids and the version number, 02 D19) is known only after the handler ran, so the
/// handler stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it.
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="IfMatch">The <c>If-Match</c> header value: the <c>fs-</c> ETag of the draft the client read.</param>
[Audited("episode.fact_sheet_approved", Category.Episodes, Visibility.User)]
public sealed record ApproveFactSheetCommand(Guid EpisodeId, string IfMatch)
    : ICommand<ErrorOr<FactSheetSummaryDto>>, IAuditData
{
    /// <summary>The version the handler approved; stays <see langword="null"/> for a failure.</summary>
    [JsonIgnore]
    public FactSheetApprovedOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the episode is the entity; ids and numbers only (02 D19).</summary>
    /// <returns>The projection, or <see langword="null"/> when nothing was approved.</returns>
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

/// <summary>The audit-relevant facts of an approval.</summary>
/// <param name="FactSheetId">The approved version.</param>
/// <param name="Version">Its 1-based number.</param>
public sealed record FactSheetApprovedOutcome(Guid FactSheetId, int Version);
