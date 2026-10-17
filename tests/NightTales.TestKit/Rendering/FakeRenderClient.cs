using ErrorOr;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.TestKit.Rendering;

/// <summary>The <see cref="IRenderClient"/> call a <see cref="FakeRenderClient"/> recorded.</summary>
public enum RenderClientMethod
{
    /// <summary><see cref="IRenderClient.SubmitAsync"/>.</summary>
    Submit,

    /// <summary><see cref="IRenderClient.GetStatusAsync"/>.</summary>
    GetStatus,

    /// <summary><see cref="IRenderClient.CancelAsync"/>.</summary>
    Cancel,

    /// <summary><see cref="IRenderClient.DeleteAsync"/>.</summary>
    Delete,
}

/// <summary>One recorded <see cref="FakeRenderClient"/> call: the method, the node, the task id and, for a
/// submit, the body that went to the adapter.</summary>
/// <param name="Method">Which client method ran.</param>
/// <param name="Node">The node the call addressed.</param>
/// <param name="TaskId">The <c>adapter_task_id</c> of a status, cancel or delete call; <c>null</c> for a submit.</param>
/// <param name="Body">The submit body; <c>null</c> for every other method.</param>
public sealed record RenderClientCall(
    RenderClientMethod Method,
    WorkerNode Node,
    string? TaskId,
    AdapterSubmitRequestV1? Body);

/// <summary>
/// A scripted <see cref="IRenderClient"/> for the submit and poll tests of 09.6.3-T1: every method answers from
/// its own FIFO queue, records what it was called with, and never opens a socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scripting.</b> <c>Enqueue*</c> appends one answer per call; an exhausted queue repeats its <em>last</em>
/// answer instead of throwing, so a poll loop of unknown length can be scripted with one terminal status. A
/// method that was never scripted throws <see cref="InvalidOperationException"/> — an unexpected adapter call is
/// a test bug, not an empty answer. The <c>Deferred</c>, <c>Transient</c>, <c>Terminal</c> and <c>Accepted</c>
/// helpers build the answers through <see cref="RenderClientErrors"/>, so the fake carries the same failure
/// typing the real client does.
/// </para>
/// <para>
/// <b>The WT55 race.</b> <see cref="OnSubmit"/> runs between the recorded call and the answer, which is the
/// window in which the real adapter has accepted a task while the job's row may still change: a test sets
/// <see cref="WriteModel"/> to the caller's <see cref="IAppDbContext"/> and mutates the tracked row there (for
/// example <c>row.RequestCancel(now)</c> then <c>row.Cancel(now)</c>) to exercise the re-check.
/// <see cref="OnStatus"/> is the same window on the poll path (<c>PollRenderJob</c>), which takes no row lock:
/// a test writes the row from another connection there, or blocks both polls of a concurrent-tick test until
/// they have read the row.
/// </para>
/// <para>
/// <b>Thread safety.</b> The queues, the counters and the call log are guarded by one lock, because the poller
/// resolves the client once and runs several jobs in parallel.
/// </para>
/// </remarks>
public sealed class FakeRenderClient : IRenderClient
{
    private readonly Lock _gate = new();
    private readonly Queue<ErrorOr<AdapterAcceptedV1>> _submits = [];
    private readonly Queue<ErrorOr<RenderStatusV1>> _statuses = [];
    private readonly Queue<ErrorOr<Success>> _cancels = [];
    private readonly Queue<ErrorOr<Success>> _deletes = [];
    private readonly List<RenderClientCall> _calls = [];
    private ErrorOr<AdapterAcceptedV1>? _lastSubmit;
    private ErrorOr<RenderStatusV1>? _lastStatus;
    private ErrorOr<Success>? _lastCancel;
    private ErrorOr<Success>? _lastDelete;
    private int _submitCalls;
    private int _statusCalls;
    private int _cancelCalls;
    private int _deleteCalls;

    /// <summary>
    /// The caller's write model, for <see cref="OnSubmit"/> only; the fake itself never reads or writes it.
    /// </summary>
    public IAppDbContext? WriteModel { get; set; }

    /// <summary>
    /// Runs after the submit call is recorded and before its scripted answer is returned: the hook a test uses
    /// to change the row while the adapter call is "in flight" (the WT55 race).
    /// </summary>
    public Func<WorkerNode, AdapterSubmitRequestV1, ValueTask>? OnSubmit { get; set; }

    /// <summary>
    /// Runs after the status call is recorded and before its scripted answer is returned: the hook a test uses
    /// to write the row from another connection while the poll is in flight — the poll path takes no row lock,
    /// so its commit loses to a concurrent cancel — or to hold two polls at the same point so both read the row
    /// before either settles it.
    /// </summary>
    public Func<WorkerNode, string, ValueTask>? OnStatus { get; set; }

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<RenderClientCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many submits ran.</summary>
    public int SubmitCalls
    {
        get
        {
            lock (_gate)
            {
                return _submitCalls;
            }
        }
    }

    /// <summary>How many status calls ran.</summary>
    public int StatusCalls
    {
        get
        {
            lock (_gate)
            {
                return _statusCalls;
            }
        }
    }

    /// <summary>How many cancels ran.</summary>
    public int CancelCalls
    {
        get
        {
            lock (_gate)
            {
                return _cancelCalls;
            }
        }
    }

    /// <summary>How many deletes ran.</summary>
    public int DeleteCalls
    {
        get
        {
            lock (_gate)
            {
                return _deleteCalls;
            }
        }
    }

    /// <summary>Builds a successful submit answer for <paramref name="taskId"/> (a fresh version 7 id when null).</summary>
    /// <param name="taskId">The task id the adapter reports.</param>
    /// <param name="state">The adapter's <c>state</c> string.</param>
    /// <returns>The answer to pass to <see cref="EnqueueSubmit"/>.</returns>
    public static ErrorOr<AdapterAcceptedV1> Accepted(Guid? taskId = null, string state = "queued") =>
        new AdapterAcceptedV1(taskId ?? Guid.CreateVersion7(), state);

    /// <summary>Builds a <see cref="RenderClientFailureKind.Deferred"/> answer.</summary>
    /// <param name="code">The D5 outcome code, e.g. <c>adapter_capacity_full</c>.</param>
    /// <param name="retryAfter">The adapter's own wait, when it gave one.</param>
    /// <returns>The answer to pass to <see cref="EnqueueSubmit"/> or <see cref="EnqueueStatus"/>.</returns>
    public static ErrorOr<AdapterAcceptedV1> Deferred(string code, TimeSpan? retryAfter = null) =>
        Failure(new RenderClientFailure(RenderClientFailureKind.Deferred, code, "scripted", Alert: false, retryAfter));

    /// <summary>Builds a <see cref="RenderClientFailureKind.TransientFailure"/> answer.</summary>
    /// <param name="code">The D5 outcome code.</param>
    /// <returns>The answer to pass to <see cref="EnqueueSubmit"/>.</returns>
    public static ErrorOr<AdapterAcceptedV1> Transient(string code = "render_node_error") =>
        Failure(new RenderClientFailure(RenderClientFailureKind.TransientFailure, code, "scripted", Alert: false, null));

    /// <summary>Builds a <see cref="RenderClientFailureKind.TerminalFailure"/> answer.</summary>
    /// <param name="code">The D5 outcome code.</param>
    /// <param name="alert">Whether the failure asks the job to raise the operator alarm.</param>
    /// <returns>The answer to pass to <see cref="EnqueueSubmit"/>.</returns>
    public static ErrorOr<AdapterAcceptedV1> Terminal(string code, bool alert = false) =>
        Failure(new RenderClientFailure(RenderClientFailureKind.TerminalFailure, code, "scripted", alert, null));

    /// <summary>Scripts the next submit answer.</summary>
    /// <param name="answer">The answer, normally one of <see cref="Accepted"/>, <see cref="Deferred"/>,
    /// <see cref="Transient"/> or <see cref="Terminal"/>.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeRenderClient EnqueueSubmit(ErrorOr<AdapterAcceptedV1> answer)
    {
        lock (_gate)
        {
            _submits.Enqueue(answer);
        }

        return this;
    }

    /// <summary>Scripts the next status answer.</summary>
    /// <param name="answer">The status to report.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeRenderClient EnqueueStatus(ErrorOr<RenderStatusV1> answer)
    {
        lock (_gate)
        {
            _statuses.Enqueue(answer);
        }

        return this;
    }

    /// <summary>Scripts the next cancel answer.</summary>
    /// <param name="answer">The answer; <c>default</c> is a success.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeRenderClient EnqueueCancel(ErrorOr<Success> answer)
    {
        lock (_gate)
        {
            _cancels.Enqueue(answer);
        }

        return this;
    }

    /// <summary>Scripts the next delete answer.</summary>
    /// <param name="answer">The answer; <c>default</c> is a success.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeRenderClient EnqueueDelete(ErrorOr<Success> answer)
    {
        lock (_gate)
        {
            _deletes.Enqueue(answer);
        }

        return this;
    }

    /// <summary>
    /// Scripts one failure answer for <paramref name="method"/>: the failure is wrapped with
    /// <see cref="RenderClientErrors.ToError"/>, so the job reads the same typed failure the real client
    /// produces.
    /// </summary>
    /// <param name="method">The method that should fail.</param>
    /// <param name="failure">The failure, e.g. <c>new RenderClientFailure(RenderClientFailureKind.Deferred, "adapter_node_unhealthy", Detail: null, Alert: false, null)</c>.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeRenderClient EnqueueFailure(RenderClientMethod method, RenderClientFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        lock (_gate)
        {
            switch (method)
            {
                case RenderClientMethod.Submit:
                    _submits.Enqueue(RenderClientErrors.ToError(failure));
                    break;
                case RenderClientMethod.GetStatus:
                    _statuses.Enqueue(RenderClientErrors.ToError(failure));
                    break;
                case RenderClientMethod.Cancel:
                    _cancels.Enqueue(RenderClientErrors.ToError(failure));
                    break;
                case RenderClientMethod.Delete:
                    _deletes.Enqueue(RenderClientErrors.ToError(failure));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown render client method.");
            }
        }

        return this;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<AdapterAcceptedV1>> SubmitAsync(
        WorkerNode node,
        AdapterSubmitRequestV1 body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(body);

        ErrorOr<AdapterAcceptedV1> answer;
        lock (_gate)
        {
            _submitCalls++;
            _calls.Add(new RenderClientCall(RenderClientMethod.Submit, node, TaskId: null, body));
            answer = Next(_submits, ref _lastSubmit, RenderClientMethod.Submit);
        }

        if (OnSubmit is { } onSubmit)
        {
            await onSubmit(node, body).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        return answer;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<RenderStatusV1>> GetStatusAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        ErrorOr<RenderStatusV1> answer;
        lock (_gate)
        {
            _statusCalls++;
            _calls.Add(new RenderClientCall(RenderClientMethod.GetStatus, node, taskId, Body: null));
            answer = Next(_statuses, ref _lastStatus, RenderClientMethod.GetStatus);
        }

        if (OnStatus is { } onStatus)
        {
            await onStatus(node, taskId).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        return answer;
    }

    /// <inheritdoc />
    public Task<ErrorOr<Success>> CancelAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _cancelCalls++;
            _calls.Add(new RenderClientCall(RenderClientMethod.Cancel, node, taskId, Body: null));

            return Task.FromResult(Next(_cancels, ref _lastCancel, RenderClientMethod.Cancel));
        }
    }

    /// <inheritdoc />
    public Task<ErrorOr<Success>> DeleteAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _deleteCalls++;
            _calls.Add(new RenderClientCall(RenderClientMethod.Delete, node, taskId, Body: null));

            return Task.FromResult(Next(_deletes, ref _lastDelete, RenderClientMethod.Delete));
        }
    }

    private static ErrorOr<AdapterAcceptedV1> Failure(RenderClientFailure failure) =>
        RenderClientErrors.ToError(failure);

    /// <summary>
    /// Takes the next answer, or repeats the last one when the queue ran out; a method that was never scripted
    /// is the test's mistake and is reported as such. The caller holds <see cref="_gate"/>.
    /// </summary>
    private static T Next<T>(Queue<T> answers, ref T? last, RenderClientMethod method)
        where T : struct
    {
        if (answers.Count > 0)
        {
            last = answers.Dequeue();

            return last.Value;
        }

        if (last is { } previous)
        {
            return previous;
        }

        throw new InvalidOperationException(
            $"The test did not script a '{method}' answer on FakeRenderClient.");
    }
}
