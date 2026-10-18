using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering;

/// <summary>
/// A render attempt reached the terminal <c>succeeded</c> state and its output was stored (02 CR-23, BD7, WT45).
/// The outbox stores it as <c>render_succeeded.v1</c> in the same transaction as the <c>assets</c> rows, the
/// episode's move to <c>video_review</c>, the settlement of the attempt's reservation and the audit row, which is
/// the event 02.8.2-T1's video gate and 09.8.1-T1's realtime mirror read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids only.</b> The payload carries the episode and the attempt, exactly like <see cref="RenderFailed"/>:
/// never a blob key, a URL, the SHA-256 or a byte count, so no consumer of this event needs storage access and no
/// row of the outbox can leak a storage location or a content fragment (D11/D19, 05 P6/P8).
/// </para>
/// <para>
/// <b>Raised on the succeeded row, once.</b> The ingest is delivered at least once, so its own re-run is a logged
/// no-op before any write (the row is already terminal); the event is therefore raised by the one transition that
/// turns the row <c>ingesting</c> → <c>succeeded</c>, never by a retry of the steps that follow it.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode whose render is stored and waiting for the owner's review.</param>
/// <param name="RenderJobId">The <c>render_jobs</c> row that produced the stored output.</param>
/// <param name="OccurredAt">When the attempt ended, from the Worker's clock.</param>
[OutboxEvent("render_succeeded.v1")]
public sealed record RenderSucceeded(
    Guid EpisodeId,
    Guid RenderJobId,
    DateTimeOffset OccurredAt) : IDomainEvent;
