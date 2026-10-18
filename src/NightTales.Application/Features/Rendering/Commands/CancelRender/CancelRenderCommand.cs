using System.Globalization;
using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Rendering.Commands.CancelRender;

/// <summary>
/// <c>DELETE /api/v1/render-jobs/{id}</c> (09 README BD8, 09.6.4-T1): stop one render attempt of the caller. A
/// <c>queued</c> job is cancelled at once — terminal row, refunded reservation, episode back to
/// <c>story_approved</c>; a <c>submitted</c>/<c>running</c> one is only <em>asked</em> to cancel, because the
/// refund happens when the poller records the adapter's terminal <c>cancelled</c> and never in the request path
/// ([RULED — README §B10 WT51]); an <c>ingesting</c> job is refused with 409 <c>episode_invalid_state</c>.
/// </summary>
/// <remarks>
/// <para>
/// The command is ids-only (D12) and so is its audit row (D19): the render params, the quality report, the story
/// text and the node's own text are never part of it. The job is addressed through the D10 tenant filter, so
/// another user's <c>render_job_id</c>, an unknown id and an anonymous scope all answer 404 <c>not_found</c> —
/// never 403, and never a way to tell the two apart. The Api performs no node call on this route: the adapter's
/// <c>POST /v1/renders/{task_id}/cancel</c> is the Worker's outbox handler's job (BD11/D11).
/// </para>
/// <para>
/// <see cref="Outcome"/> is set by the handler and read by the audit behaviour, which writes the row inside the
/// unit of work (BD15). It stays <see langword="null"/> for the idempotent repeats — a second <c>DELETE</c> of an
/// already-cancelled job, or of a job whose cancellation was already requested — so a repeat writes no second
/// audit row, exactly as the repeat writes no second outbox row and no second refund.
/// </para>
/// </remarks>
/// <param name="RenderJobId">The render job, from the route; the job must belong to the caller (D10).</param>
[Audited("render.cancelled", Category.Episodes, Visibility.User)]
public sealed record CancelRenderCommand(Guid RenderJobId) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>
    /// What this call changed, or <see langword="null"/> when it changed nothing: the idempotent repeat writes
    /// no audit row (the <c>DismissSourceCandidateCommand</c> shape), so one cancel yields exactly one row.
    /// </summary>
    [JsonIgnore]
    public CancelOutcome? Outcome { get; set; }

    /// <summary>
    /// The row's projection (09 README BD15/BD16): the render job is the entity, the caller is the subject, and
    /// the summary is the two ids — the same keys the Worker's own <c>render.cancelled</c> row carries, so the
    /// activity feed reads one shape per action (D19: ids only, never content).
    /// </summary>
    /// <returns>The projection, or <see langword="null"/> when this call changed nothing.</returns>
    public AuditData? ToAuditData() => Outcome is { } outcome
        ? new AuditData(
            null,
            RenderFailureRecorder.EntityType,
            RenderJobId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["episodeId"] = outcome.EpisodeId.ToString("D", CultureInfo.InvariantCulture),
                ["renderJobId"] = RenderJobId.ToString("D", CultureInfo.InvariantCulture),
            })
        : null;
}

/// <summary>The audit-relevant fact of a cancel this call applied: which episode's attempt it ended.</summary>
/// <param name="EpisodeId">The episode of the render job the call cancelled or asked to cancel.</param>
public sealed record CancelOutcome(Guid EpisodeId);
