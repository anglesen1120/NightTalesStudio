using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;

namespace NightTales.Application.Features.Rendering.Outbox;

/// <summary>
/// Delivers <c>render_cancel_requested.v1</c> by asking the node that holds the attempt to cancel its adapter task
/// (<c>POST /v1/renders/{task_id}/cancel</c>, 05 D4, 09 README D11/D12, 09.6.4-T1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The Worker's call, never the request path.</b> The <c>DELETE /render-jobs/{id}</c> only stamps
/// <c>cancel_requested_at</c> and writes this event in its own transaction, so the Api resolves no
/// <see cref="IRenderClient"/> for a cancel and can never reach a node key; the adapter call happens here, under
/// the outbox's backoff. Nothing of the node — its base URL, its pinned certificate, its key — leaves
/// <see cref="IRenderClient"/>, and every line of this class carries ids and codes only (BD16/S4).
/// </para>
/// <para>
/// <b>The owner comes from the payload, and the scope is pinned before anything is resolved.</b> The event carries
/// the row's owner (D11/D12: the payload is ids only and this <em>is</em> the id that is needed), and the outbox
/// writes it from the same <c>IUserOwned</c> aggregate that raised the event, so no cross-user read is needed here
/// — unlike the id-only jobs, which have to resolve the owner through <c>ISystemWorkQuery</c>. The delivery's own
/// scope is pinned with <see cref="ISystemContext.For"/> <em>before</em> the first scoped service is resolved,
/// because the tenant filter is fixed when the <c>AppDbContext</c> is constructed: the load below can only ever see
/// the canceller's row, and a foreign or vanished id is a logged no-op rather than another user's render.
/// </para>
/// <para>
/// <b>The cancel has three skips.</b> A row that is not <c>submitted</c> or <c>running</c> is not the adapter
/// task's owner any more — <c>queued</c> rows are cancelled at once by the command (no event is ever written for
/// them), an <c>ingesting</c> row's output is already being stored (BD8), and a terminal row never moves again
/// (WT45/WT51). A row without an <c>adapter_task_id</c> never reached a node (the submit's WT55 re-check), so
/// there is nothing to stop and no call is ever attempted with a null task id. A row whose node is gone cannot be
/// reached again: the call is skipped and the poller's own node-error path and the watchdog decide the row. The
/// request itself needs no re-reading of <c>cancel_requested_at</c>: <c>RequestCancel</c> stamps it and raises this
/// event in the same call, the outbox row commits with the stamp, and nothing ever clears it, so a delivered event
/// always has the request on the row.
/// </para>
/// <para>
/// <b>The adapter's answer is mapped, and it never fails the render job.</b> Deferred and transient answers
/// (capacity, a restart, a transport error, a 5xx) are thrown so the dispatcher retries this delivery with backoff
/// (D11, at most ten attempts) while the row keeps <c>cancel_requested_at</c>; a terminal answer — a code the node
/// refuses, or a task the node no longer knows — is logged and swallowed, because a cancel request must never
/// fail, refund or write a row: only the poller's terminal path owns the row's outcome. A terminal adapter task
/// answers 200 as a no-op (05 D4), which is the same success as a 202.
/// </para>
/// <para>
/// <b>No write and no transaction.</b> This handler reads the row and the node in autocommit queries and opens no
/// transaction, so no pooled connection is held across the HTTP round trip, and the row's fate stays with the
/// poller: it records the terminal <c>cancelled</c>, refunds the reservation once by <c>reservation_id</c> and
/// returns the episode to <c>story_approved</c>. The adapter task is never <c>DELETE</c>d here either: the poller's
/// other terminal branches clean up the tasks they end (best effort), the ingest releases the material of a stored
/// success, and a terminal <c>cancelled</c> needs none — the adapter removes its task dir at MPT's terminal state
/// (05 D4/P4).
/// </para>
/// </remarks>
/// <param name="scopes">Creates the per-delivery scope the pinned services are resolved from.</param>
/// <param name="logger">The Worker's logger; every line carries ids and codes only.</param>
public sealed partial class RenderCancelRequestedHandler(
    IServiceScopeFactory scopes,
    ILogger<RenderCancelRequestedHandler> logger) : IOutboxHandler<RenderCancelRequested>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(
        RenderCancelRequested domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // The scope is pinned to the event's owner before the first scoped service: the tenant filter of the
        // context is fixed at construction (D10), so nothing below can read another user's row.
        await using var scope = scopes.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(domainEvent.UserId);

        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<IAppDbContext>();
        var client = services.GetRequiredService<IRenderClient>();

        var job = await db.RenderJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == domainEvent.RenderJobId, cancellationToken)
            .ConfigureAwait(false);

        if (job is null)
        {
            // A foreign id (the tenant filter hid it) or a row deleted by the retention sweep: nothing to cancel.
            LogJobMissing(domainEvent.RenderJobId);

            return;
        }

        // Step 1. Only a submitted or running row still has an adapter task this event owns: the command cancels
        // a queued row at once, an ingesting row's output is already being stored (BD8), and a terminal row —
        // the poller's own `cancelled` included — never moves again (WT45/WT51).
        if (job.Status is not (RenderJobStatus.Submitted or RenderJobStatus.Running))
        {
            LogNotCancellable(job.Id, job.Status);

            return;
        }

        // Step 2. Both are written together by MarkSubmitted, and the WT55 re-check refuses to adopt a task on a
        // cancel-requested row: without them there is no adapter task, and the request path may never send a call
        // with a null task id.
        if (job.AdapterTaskId is not { Length: > 0 } taskId || job.WorkerNodeId is not { } nodeId)
        {
            LogNoAdapterTask(job.Id);

            return;
        }

        // Step 3. The node the attempt went to, looked up and never remembered: nothing of it reaches this class
        // except the instance the client needs (11 D13).
        var node = await db.WorkerNodes
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == nodeId, cancellationToken)
            .ConfigureAwait(false);

        if (node is null)
        {
            // The node row is gone, so its task can never be reached again: the cancel is skipped — the render
            // job is not this handler's to fail, and the poller records the node error on its next tick.
            LogNodeMissing(job.Id, nodeId);

            return;
        }

        // Step 4. The one adapter call of this delivery. It is idempotent (05 D4), so a redelivery after a lost
        // answer asks the same task to cancel a second time instead of guessing.
        var answer = await client.CancelAsync(node, taskId, cancellationToken).ConfigureAwait(false);

        if (!answer.IsError)
        {
            // 202 accepted, or a 200 on a task the adapter already finished (05 D4): both are the cancel done.
            LogAdapterCancelAccepted(job.Id);

            return;
        }

        if (!RenderClientErrors.TryGetFailure(answer.FirstError, out var failure))
        {
            throw new InvalidOperationException(
                $"The cancel call of job '{job.Id}' failed with an unmapped error ('{answer.FirstError.Code}').");
        }

        switch (failure.Kind)
        {
            case RenderClientFailureKind.Deferred:
            case RenderClientFailureKind.TransientFailure:
                // The node is busy, restarting or unreachable: the dispatcher retries this delivery with backoff
                // (D11, at most ten attempts) instead of spending the request path. The row keeps its cancel
                // request; if the node never answers, the 60-minute watchdog eventually fails it (BD8).
                LogRetryable(job.Id, failure.Kind.ToString(), failure.OutcomeCode);

                throw new InvalidOperationException(
                    $"The cancel call of job '{job.Id}' was answered as '{failure.Kind}' "
                    + $"('{failure.OutcomeCode ?? answer.FirstError.Code}'); the outbox retries it.");

            case RenderClientFailureKind.TerminalFailure:
                // A contract, key or idempotency fault, or a task the node no longer knows (render_lost): nothing
                // is retried and the render job must not be failed by a cancel request — the poller's own
                // terminal path decides the row's fate on its next tick.
                if (failure.Alert)
                {
                    LogAdapterAlert(job.Id, failure.OutcomeCode, failure.Detail);
                }

                LogTerminalFailure(job.Id, failure.OutcomeCode);

                return;

            default:
                // An output kind cannot come from a cancel call: the adapter's answer is drift, so the delivery
                // is rethrown and the outbox's attempt budget bounds it (the poller treats a status answer the
                // same way).
                LogUnexpectedFailure(job.Id, failure.Kind.ToString(), failure.OutcomeCode);

                throw new InvalidOperationException(
                    $"The cancel call of job '{job.Id}' was answered as '{failure.Kind}', which a cancel "
                    + "cannot answer with.");
        }
    }

    [LoggerMessage(
        EventId = 6411,
        Level = LogLevel.Warning,
        Message = "render_cancel_job_missing renderJobId={RenderJobId}")]
    private partial void LogJobMissing(Guid renderJobId);

    [LoggerMessage(
        EventId = 6412,
        Level = LogLevel.Information,
        Message = "render_cancel_not_cancellable renderJobId={RenderJobId} status={Status}")]
    private partial void LogNotCancellable(Guid renderJobId, RenderJobStatus status);

    [LoggerMessage(
        EventId = 6413,
        Level = LogLevel.Warning,
        Message = "render_cancel_no_adapter_task renderJobId={RenderJobId}")]
    private partial void LogNoAdapterTask(Guid renderJobId);

    [LoggerMessage(
        EventId = 6414,
        Level = LogLevel.Warning,
        Message = "render_cancel_node_missing renderJobId={RenderJobId} workerNodeId={WorkerNodeId}")]
    private partial void LogNodeMissing(Guid renderJobId, Guid workerNodeId);

    [LoggerMessage(
        EventId = 6415,
        Level = LogLevel.Information,
        Message = "render_cancel_adapter_accepted renderJobId={RenderJobId}")]
    private partial void LogAdapterCancelAccepted(Guid renderJobId);

    [LoggerMessage(
        EventId = 6416,
        Level = LogLevel.Warning,
        Message = "render_cancel_retryable renderJobId={RenderJobId} kind={Kind} code={Code}")]
    private partial void LogRetryable(Guid renderJobId, string kind, string? code);

    [LoggerMessage(
        EventId = 6417,
        Level = LogLevel.Error,
        Message = "render_cancel_adapter_alert renderJobId={RenderJobId} code={Code} detail={Detail}")]
    private partial void LogAdapterAlert(Guid renderJobId, string? code, string? detail);

    [LoggerMessage(
        EventId = 6418,
        Level = LogLevel.Error,
        Message = "render_cancel_terminal_failure renderJobId={RenderJobId} code={Code}")]
    private partial void LogTerminalFailure(Guid renderJobId, string? code);

    [LoggerMessage(
        EventId = 6419,
        Level = LogLevel.Warning,
        Message = "render_cancel_unexpected_failure renderJobId={RenderJobId} kind={Kind} code={Code}")]
    private partial void LogUnexpectedFailure(Guid renderJobId, string kind, string? code);
}
