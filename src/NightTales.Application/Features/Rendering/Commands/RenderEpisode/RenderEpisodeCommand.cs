using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Rendering.Commands.RenderEpisode;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/render</c> (09 README BD5): queue exactly one render job for one approved
/// episode in a single transaction — episode row lock, the state and <c>If-Match</c> preconditions, the
/// approved story's text, the keyword lint, the image plan, the platform spend cap, the credit reservation,
/// the <c>queued</c> job row and the <c>RenderRequested</c> outbox row.
/// </summary>
/// <remarks>
/// <para>
/// The <c>If-Match</c> header is required on the route (02 D9, 09 README D7): a render reserves credit and
/// spends platform money against the story the client saw, so a stale <c>"ep-&lt;xmin&gt;"</c> must be refused
/// rather than rendering a version the client never approved. The handler compares it again with a strong
/// exact comparison, because a direct call does not pass through the route filter.
/// </para>
/// <para>
/// The audit row's <c>data</c> (ids only, 02 D19) is known only after the handler ran, so the handler stores
/// it in <see cref="Outcome"/> and <see cref="ToAuditData"/> projects it. The property is not part of the
/// request: it is excluded from the serialized message the idempotency hash is computed from.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode, from the route.</param>
/// <param name="IfMatch">The required <c>If-Match</c> header value; never the current episode ETag.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header value.</param>
[Audited("render.requested", Category.Episodes, Visibility.User)]
public sealed record RenderEpisodeCommand(
    Guid EpisodeId,
    string? IfMatch,
    string IdempotencyKey)
    : ICommand<ErrorOr<RenderJobAcceptedDto>>, IIdempotentCommand, IAuditData
{
    /// <inheritdoc />
    string? IIdempotentCommand.IdempotencyKey => IdempotencyKey;

    /// <summary>What the handler queued; set by the handler, read by the audit behaviour.</summary>
    [JsonIgnore]
    public RenderJobOutcome? Outcome { get; set; }

    /// <summary>The row's projection: the episode is the entity; ids only, never content (02 D19).</summary>
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
                    ["renderJobId"] = Outcome.RenderJobId.ToString("D", CultureInfo.InvariantCulture),
                });
}

/// <summary>The audit-relevant facts of a queued render job.</summary>
/// <param name="RenderJobId">The queued job; nothing else about a render is auditable without content.</param>
public sealed record RenderJobOutcome(Guid RenderJobId);
