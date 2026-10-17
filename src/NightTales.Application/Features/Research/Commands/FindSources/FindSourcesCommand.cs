using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Research.Commands.FindSources;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/sources/find</c> (02.7 plan §4 U1): queue one source search for one episode in a
/// single transaction — the flag and allow-list gates, the episode row lock, the state precondition, the active-run
/// gate, the platform spend cap, the credit reservation, the <c>queued</c> run row and the
/// <c>SourceFindRequested</c> outbox row.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Idempotency-Key</c> header is required on the route, because the run reserves credit and spends platform
/// money (09 README D6); a repeated key replays the first <see cref="FindSourcesAccepted"/>. The autopilot sends
/// the same command with <see cref="GenerationTrigger.Autopilot"/> and a key of its own under a system scope.
/// </para>
/// <para>
/// <see cref="Query"/> is the only field the user shapes. The validator trims it and strips control characters, and
/// <see cref="NormalizeQuery"/> is the single implementation of that rule, which the handler applies before the
/// episode transition. The query then lives in the outbox payload only (02.7 plan §3, D19): it is not part of the
/// audit row, which projects ids and money through <see cref="Outcome"/>.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="Query">The user's free-text search, or <see langword="null"/> to let the job derive one.</param>
/// <param name="Trigger">Who asked; anything but <see cref="GenerationTrigger.User"/> needs a system scope.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header value.</param>
[Audited("episode.sources_find_requested", Category.Episodes, Visibility.User)]
public sealed record FindSourcesCommand(
    Guid EpisodeId,
    string? Query,
    GenerationTrigger Trigger,
    string? IdempotencyKey)
    : ICommand<ErrorOr<FindSourcesAccepted>>, IIdempotentCommand, IAuditData
{
    /// <summary>The longest stored query, after trimming and control-character stripping (02.7 plan §3).</summary>
    public const int MaxQueryLength = Episode.MaxSourceFindQueryChars;

    /// <summary>What the handler queued; set by the handler, read by the audit behaviour.</summary>
    [JsonIgnore]
    public FindSourcesOutcome? Outcome { get; set; }

    /// <summary>
    /// The shape rule of the typed query: no control, format or line-separator characters, then trimmed, and
    /// <see langword="null"/> when nothing is left. The validator and the handler both use it, so the length the
    /// validator accepts is exactly the length the outbox stores.
    /// </summary>
    /// <param name="query">The caller's raw query, or <see langword="null"/>.</param>
    /// <returns>The storable query, or <see langword="null"/>.</returns>
    public static string? NormalizeQuery(string? query)
    {
        if (query is null)
        {
            return null;
        }

        var stripped = new char[query.Length];
        var length = 0;
        foreach (var character in query)
        {
            switch (char.GetUnicodeCategory(character))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    continue;
                default:
                    stripped[length++] = character;
                    break;
            }
        }

        var trimmed = new string(stripped, 0, length).Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>The row's projection: the episode is the entity; ids and money only (02 D19, D13).</summary>
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

/// <summary>The audit-relevant facts of a queued source search.</summary>
/// <param name="GenerationRunId">The queued run.</param>
/// <param name="WorstCaseUsd">The reserved worst case in USD.</param>
public sealed record FindSourcesOutcome(Guid GenerationRunId, decimal WorstCaseUsd);
