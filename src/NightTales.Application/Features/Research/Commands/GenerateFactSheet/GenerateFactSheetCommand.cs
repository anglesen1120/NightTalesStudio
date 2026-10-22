using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Research.Commands.GenerateFactSheet;

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
/// platform money (09 README D6); a repeated key replays the first <see cref="FactSheetRunDto"/>. The autopilot
/// sends the same command with <see cref="GenerationTrigger.Autopilot"/> and a key of its own under a system
/// scope (02.8.3 plan §3.5 rule "GenerationTrigger.Autopilot on every generation_runs row this feature
/// creates"; STATUS S71-6).
/// </para>
/// <para>
/// The audit row's <c>data</c> (ids and numbers only, 02 D19) is known only after the handler ran, so the
/// handler stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it. The property is not
/// part of the request: it is excluded from the serialized message the idempotency hash is computed from.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header value.</param>
/// <param name="Trigger">
/// Who asked; anything but <see cref="GenerationTrigger.User"/> needs a system scope. It is the last parameter
/// rather than the second (where <c>GenerateStoryCommand</c> puts it) so the route's and the tests' positional
/// <c>(episodeId, key)</c> calls keep compiling; a caller that names the trigger uses the named argument.
/// </param>
[Audited("episode.fact_sheet_generation_requested", Category.Episodes, Visibility.User)]
public sealed record GenerateFactSheetCommand(
    Guid EpisodeId,
    string IdempotencyKey,
    GenerationTrigger Trigger = GenerationTrigger.User)
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
                    ["trigger"] = Outcome.Trigger,
                    ["worstCaseUsd"] = Outcome.WorstCaseUsd,
                });
}

/// <summary>The audit-relevant facts of a queued fact-sheet run.</summary>
/// <param name="GenerationRunId">The queued run.</param>
/// <param name="Trigger">
/// The trigger's wire value (<c>user</c>, <c>autopilot</c>, <c>system</c>), exactly as the story command's audit row
/// carries it: the audit writer serializes the summary with default options, so an enum would land as a number.
/// </param>
/// <param name="WorstCaseUsd">The reserved worst case in USD.</param>
public sealed record FactSheetRunOutcome(Guid GenerationRunId, string Trigger, decimal WorstCaseUsd);
