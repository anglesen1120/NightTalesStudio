using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Stories.Commands.GenerateStory;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/story/generate</c> (02.2.2 plan §4 U1): queue one story generation for one episode
/// in a single transaction — episode row lock, rolling-24 h limit, platform spend cap, credit reservation, the
/// <c>queued</c> run row and the <c>StoryGenerationRequested</c> outbox row.
/// </summary>
/// <remarks>
/// <para>
/// No request body and no <c>If-Match</c> (02 D9): the length tier and the brief are episode columns. The
/// <c>Idempotency-Key</c> header is required on the route; the autopilot sends the same command with
/// <see cref="GenerationTrigger.Autopilot"/> and a key of its own under a system scope.
/// </para>
/// <para>
/// The audit row's <c>data</c> (ids and numbers only, 02 D19) is known only after the handler ran, so the handler
/// stores it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it. The property is not part of the
/// request: it is excluded from the serialized message the idempotency hash is computed from.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="Trigger">Who asked; anything but <see cref="GenerationTrigger.User"/> needs a system scope.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header value.</param>
[Audited("episode.story_generation_requested", Category.Episodes, Visibility.User)]
public sealed record GenerateStoryCommand(
    Guid EpisodeId,
    GenerationTrigger Trigger = GenerationTrigger.User,
    string? IdempotencyKey = null)
    : ICommand<ErrorOr<GenerateStoryAccepted>>, IIdempotentCommand, IAuditData
{
    /// <summary>What the handler queued; set by the handler, read by the audit behaviour.</summary>
    [JsonIgnore]
    public GenerateStoryOutcome? Outcome { get; set; }

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
                    ["tier"] = Outcome.Tier,
                    ["worstCaseUsd"] = Outcome.WorstCaseUsd,
                });
}

/// <summary>The audit-relevant facts of a queued generation.</summary>
/// <param name="GenerationRunId">The queued run.</param>
/// <param name="Trigger">The trigger's wire value (<c>user</c>, <c>autopilot</c>, <c>system</c>).</param>
/// <param name="Tier">The length tier's wire value.</param>
/// <param name="WorstCaseUsd">The reserved worst case in USD.</param>
public sealed record GenerateStoryOutcome(Guid GenerationRunId, string Trigger, string Tier, decimal WorstCaseUsd);
