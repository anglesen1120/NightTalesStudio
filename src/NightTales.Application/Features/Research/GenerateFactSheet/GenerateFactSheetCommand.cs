using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/fact-sheet/generate</c> (02.5.3 plan §4.1 U4.1): queue one fact-sheet
/// extraction for one episode in a single transaction — episode row lock, the true-crime and state
/// preconditions, the fetched-source gate, the active-run gate, the platform spend cap, the credit
/// reservation, the <c>queued</c> run row and the <c>FactSheetGenerationRequested</c> outbox row.
/// </summary>
/// <remarks>
/// <para>
/// No request body and no <c>If-Match</c>: everything the run needs is an episode column or a source row.
/// The <c>Idempotency-Key</c> header is required on the route, because the run reserves credit and spends
/// platform money (09 README D6); a repeated key replays the first <see cref="FactSheetRunDto"/>.
/// </para>
/// <para>
/// The audit row's <c>data</c> (ids and numbers only, 02 D19) is known only after the handler ran, so the
/// handler stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it. The property is not
/// part of the request: it is excluded from the serialized message the idempotency hash is computed from.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header value.</param>
[Audited("episode.fact_sheet_generation_requested", Category.Episodes, Visibility.User)]
public sealed record GenerateFactSheetCommand(
    Guid EpisodeId,
    string IdempotencyKey)
    : ICommand<ErrorOr<FactSheetRunDto>>, IIdempotentCommand, IAuditData
{
    /// <summary>What the handler queued; set by the handler, read by the audit behaviour.</summary>
    [JsonIgnore]
    public FactSheetRunOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the episode is the entity; ids and numbers only (02 D19).</summary>
    /// <returns>The projection, or <see langword="null"/> when the handler queued nothing.</returns>
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
                    ["generationRunId"] = Outcome.GenerationRunId.ToString("D", CultureInfo.InvariantCulture),
                    ["worstCaseUsd"] = Outcome.WorstCaseUsd,
                });
}

/// <summary>The audit-relevant facts of a queued fact-sheet run.</summary>
/// <param name="GenerationRunId">The queued run.</param>
/// <param name="WorstCaseUsd">The reserved worst case in USD.</param>
public sealed record FactSheetRunOutcome(Guid GenerationRunId, decimal WorstCaseUsd);
