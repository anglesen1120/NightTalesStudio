using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering;

/// <summary>
/// A creator asked for an in-flight render to be cancelled (BD8, 09.6.4-T1). The outbox stores it as
/// <c>render_cancel_requested.v1</c> in the command's transaction and the Worker's handler asks the render node to
/// cancel the adapter task; the payload is ids only (D11/D12). The refund is not this event's business: it happens
/// once, when the poller records the terminal <c>cancelled</c> (WT51).
/// </summary>
/// <param name="RenderJobId">The <c>render_jobs</c> row whose adapter task is to be cancelled.</param>
/// <param name="EpisodeId">The episode being rendered.</param>
/// <param name="UserId">The owner the Worker handler runs as (<c>IUserOwned.UserId</c>).</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("render_cancel_requested.v1")]
public sealed record RenderCancelRequested(
    Guid RenderJobId,
    Guid EpisodeId,
    Guid UserId,
    DateTimeOffset OccurredAt) : IDomainEvent;
