using ErrorOr;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The one way the backend talks to a node's <c>mpt-adapter</c> over the frozen <c>render-node.v1</c>
/// contract (05 README D4, 09 README BD6): the submit call, the status read, the cancel and the
/// post-ingest delete. The Infrastructure implementation holds the HTTP details; this port carries none.
/// </summary>
/// <remarks>
/// <para>
/// Every call is routed through <see cref="IRenderNodeConnectionFactory"/>, so the node's base URL, its
/// pinned TLS certificate and the per-node <c>x-api-key</c> are decided in exactly one place (11 README
/// D13/F13). Neither the key nor the base URL may reach a log line or an exception message raised by an
/// implementation (09 README BD16/S4).
/// </para>
/// <para>
/// A failure is never a raw HTTP status here: an implementation maps the answer with the
/// <c>AdapterErrorMapper</c> into a <see cref="RenderClientFailure"/> and returns it through
/// <see cref="RenderClientErrors.ToError"/>, so a caller can tell a deferred call (reschedule, spend no
/// retry budget) from a transient one and from a terminal job failure (05 D5, 09.6.3-T1).
/// </para>
/// </remarks>
public interface IRenderClient
{
    /// <summary>
    /// <c>POST /v1/renders</c>: submits <paramref name="body"/> and returns the adapter's task id. The call
    /// is idempotent by <c>render_job_id</c> plus the body's SHA-256 (05 D4), so a retry after a lost answer
    /// is safe.
    /// </summary>
    /// <param name="node">The node the selector picked (11.3.3-T2).</param>
    /// <param name="body">The submit body built by <c>RenderSubmitMapper</c> (05.1.2-T1).</param>
    /// <param name="ct">The caller's cancellation; a cancelled call is never retried.</param>
    /// <returns>The accepted task, or a mapped failure.</returns>
    Task<ErrorOr<AdapterAcceptedV1>> SubmitAsync(WorkerNode node, AdapterSubmitRequestV1 body, CancellationToken ct);

    /// <summary><c>GET /v1/renders/{task_id}</c>: the task's state, stage, percent and per-output reports (05 D4).</summary>
    /// <param name="node">The node the task was submitted to.</param>
    /// <param name="taskId">The adapter's task id, as returned by <see cref="SubmitAsync"/>.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The status body, or a mapped failure.</returns>
    Task<ErrorOr<RenderStatusV1>> GetStatusAsync(WorkerNode node, string taskId, CancellationToken ct);

    /// <summary>
    /// <c>POST /v1/renders/{task_id}/cancel</c>: asks the node to stop the task. The adapter answers 202, or
    /// 200 on a task that already finished, and the call is idempotent, so it is retried (05 D4, WT55).
    /// </summary>
    /// <param name="node">The node the task was submitted to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns><see cref="Success"/> when the node accepted the cancel, or a mapped failure.</returns>
    Task<ErrorOr<Success>> CancelAsync(WorkerNode node, string taskId, CancellationToken ct);

    /// <summary>
    /// <c>DELETE /v1/renders/{task_id}</c>: releases the node's material after the output was committed
    /// (05 D4, WT55). An implementation runs this on its no-retry client — a lost answer is retried by the
    /// caller, never inside a pipeline that cannot tell a replay from a duplicate.
    /// </summary>
    /// <param name="node">The node the task was submitted to.</param>
    /// <param name="taskId">The adapter's task id.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns><see cref="Success"/> when the node released the task, or a mapped failure.</returns>
    Task<ErrorOr<Success>> DeleteAsync(WorkerNode node, string taskId, CancellationToken ct);
}
