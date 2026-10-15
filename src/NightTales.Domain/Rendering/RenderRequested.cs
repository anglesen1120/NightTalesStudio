using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering;

/// <summary>
/// A creator asked for a render (BD5, W2 CR-2). The outbox stores it as <c>render_requested.v1</c> in the command's
/// transaction and the Worker dispatcher turns it into <c>SubmitRenderJob(renderJobId)</c> (09.6.3-T1); the payload is
/// ids only (D11/D19), which is why the job takes no content argument.
/// </summary>
/// <param name="RenderJobId">The <c>render_jobs</c> row to submit.</param>
/// <param name="EpisodeId">The episode being rendered.</param>
/// <param name="UserId">The owner the job runs as (<c>IUserOwned.UserId</c>, the <c>ISystemContext.For</c> of the job).</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("render_requested.v1")]
public sealed record RenderRequested(
    Guid RenderJobId,
    Guid EpisodeId,
    Guid UserId,
    DateTimeOffset OccurredAt) : IDomainEvent;
