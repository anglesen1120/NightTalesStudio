using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// What one node call produced once the pipeline has finished with it: the status, the body up to the read
/// cap (empty when the answer was larger) and the parsed <c>Retry-After</c>. The pipelines of
/// <see cref="RenderClientResilience"/> are typed on it, so the retry and the breaker judge a status and a
/// count and never a body.
/// </summary>
/// <param name="Status">The response status.</param>
/// <param name="Body">The body, or empty when the answer carried none or one over the cap.</param>
/// <param name="RetryAfter">The adapter's <c>Retry-After</c>, or <c>null</c> when it sent none.</param>
internal sealed record AdapterCallResult(int Status, byte[] Body, TimeSpan? RetryAfter);

/// <summary>
/// The one type-safe client of a node's <c>mpt-adapter</c> (09 README BD6): submit, status, cancel and delete
/// over <c>render-node.v1</c>, with every failure mapped in one place
/// (<see cref="AdapterErrorMapper"/>) and the node's base URL, key and pinned certificate owned by
/// <see cref="IRenderNodeConnectionFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The three calls the adapter may safely repeat (submit, status, cancel) run on the retrying pipeline
/// (<see cref="RetryingClientName"/>); the DELETE runs on <see cref="NoRetryClientName"/>, which carries the
/// same timeouts and breaker and no retry strategy, because a transport failure has no request to inspect
/// (task §Design, WT55).
/// </para>
/// <para>
/// Nothing here logs or throws a node secret: a log line carries the node's name, the task id, a status and a
/// fixed reason, and every sentence the caller can read is the mapper's own (BD16/S4).
/// </para>
/// </remarks>
/// <param name="connectionFactory">The only producer of node clients (11 README F13).</param>
/// <param name="pipelines">The two <see cref="RenderClientResilience"/> pipelines this client routes to.</param>
/// <param name="renderNodes">The <c>RenderNodes</c> section, for the health-poll interval a deferred answer waits.</param>
/// <param name="logger">The client's log; it carries no node secret and no URL.</param>
public sealed partial class MptRenderClient(
    IRenderNodeConnectionFactory connectionFactory,
    ResiliencePipelineProvider<string> pipelines,
    IOptions<RenderNodesOptions> renderNodes,
    ILogger<MptRenderClient> logger) : IRenderClient
{
    /// <summary>The pipeline and client name of every call that may be repeated (BD6).</summary>
    public const string RetryingClientName = "render-node";

    /// <summary>The pipeline and client name of the DELETE, which is never retried (WT55).</summary>
    public const string NoRetryClientName = "render-node-no-retry";

    /// <summary>The media type of every request body (05 D4).</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>The header a deferred answer carries its wait in (05 D4).</summary>
    private const string RetryAfterHeaderName = "Retry-After";

    /// <summary>The most of an answer that is ever buffered; an answer past it is read as no body at all.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>The read chunk of one answer body.</summary>
    private const int ChunkBytes = 16 * 1024;

    /// <summary>The longest accepted task id: an adapter task id is a UUID, so anything longer is not one.</summary>
    private const int TaskIdMaxLength = 64;

    /// <summary>The reason a refused task id carries; a code, never adapter text.</summary>
    private const string TaskIdInvalidDetail = "render_task_id_invalid";

    /// <inheritdoc />
    public async Task<ErrorOr<AdapterAcceptedV1>> SubmitAsync(
        WorkerNode node,
        AdapterSubmitRequestV1 body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(body);

        // One serialisation outside the retry loop: every attempt replays the same render_job_id and the same
        // bytes, which is exactly what the adapter's idempotency check keys on (05 D4).
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, RenderNodeJson.Tolerant);

        var call = await CallAsync(
            node,
            HttpMethod.Post,
            new Uri(RenderNodeRoutes.Renders, UriKind.Relative),
            payload,
            taskSubmitted: false,
            noRetry: false,
            ct);

        if (call.IsError)
        {
            return call.Errors;
        }

        if (Parse<AdapterAcceptedV1>(call.Value.Body) is not { } accepted)
        {
            return Unreadable(node, call.Value.Status, taskSubmitted: false);
        }

        LogAccepted(node.Name, accepted.TaskId, accepted.State);
        return accepted;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<RenderStatusV1>> GetStatusAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!IsSafeTaskId(taskId))
        {
            return RefusedTaskId(node);
        }

        // The task exists: a 404 adapter_task_unknown here is a lost render, not a node fault (05 D5).
        var call = await CallAsync(
            node,
            HttpMethod.Get,
            Route(RenderNodeRoutes.RenderById, taskId),
            payload: null,
            taskSubmitted: true,
            noRetry: false,
            ct);

        if (call.IsError)
        {
            return call.Errors;
        }

        if (Parse<RenderStatusV1>(call.Value.Body) is not { } status)
        {
            return Unreadable(node, call.Value.Status, taskSubmitted: true);
        }

        LogStatus(node.Name, taskId, status.State, status.Percent);
        return status;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> CancelAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!IsSafeTaskId(taskId))
        {
            return RefusedTaskId(node);
        }

        // 202 with a task the adapter still owns and 200 with one it already finished are both a cancel that
        // took effect (05 D4), so any 2xx is this call's success and the body carries nothing to read.
        var call = await CallAsync(
            node,
            HttpMethod.Post,
            Route(RenderNodeRoutes.RenderCancel, taskId),
            payload: null,
            taskSubmitted: true,
            noRetry: false,
            ct);

        if (call.IsError)
        {
            return call.Errors;
        }

        LogCancelled(node.Name, taskId);
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> DeleteAsync(WorkerNode node, string taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!IsSafeTaskId(taskId))
        {
            return RefusedTaskId(node);
        }

        // The one call on the no-retry pipeline: a DELETE that is repeated could remove a task another attempt
        // of the same job has just resubmitted, and no predicate can identify a DELETE from a transport
        // failure (task §Design, WT55).
        var call = await CallAsync(
            node,
            HttpMethod.Delete,
            Route(RenderNodeRoutes.RenderById, taskId),
            payload: null,
            taskSubmitted: true,
            noRetry: true,
            ct);

        if (call.IsError)
        {
            return call.Errors;
        }

        LogDeleted(node.Name, taskId);
        return Result.Success;
    }

    /// <summary>
    /// Runs one node call inside its pipeline and maps everything the HTTP layer produces: the answer of a 2xx
    /// (the caller decodes its body), the adapter's envelope of anything else, and the three faults that never
    /// produce a response. The caller's cancellation is not a failure and leaves as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="node">The node to call; its base URL, key and pin come from the connection factory.</param>
    /// <param name="method">The verb: POST, GET or DELETE, mapped by the payload and the route the caller chose.</param>
    /// <param name="route">The route built from the contract's literals, with any task id already escaped.</param>
    /// <param name="payload">The serialised body, or <c>null</c> for the three body-less calls.</param>
    /// <param name="taskSubmitted">Whether the call concerns a task the node accepted (05 D5's 404 rule).</param>
    /// <param name="noRetry">Whether the call must use the no-retry pipeline (the DELETE).</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The answer's status and body, or the mapped failure.</returns>
    private async Task<ErrorOr<AdapterCallResult>> CallAsync(
        WorkerNode node,
        HttpMethod method,
        Uri route,
        byte[]? payload,
        bool taskSubmitted,
        bool noRetry,
        CancellationToken ct)
    {
        var pipeline = pipelines.GetPipeline<AdapterCallResult>(noRetry ? NoRetryClientName : RetryingClientName);
        var healthPoll = TimeSpan.FromSeconds(renderNodes.Value.HealthPollSeconds);

        AdapterCallResult call;

        try
        {
            call = await pipeline.ExecuteAsync(
                async token =>
                {
                    // The factory decides the base address, the x-api-key header and the pinned certificate
                    // (11 README F13); this class never builds a transport of its own.
                    using var client = connectionFactory.Create(node);

                    // The pipeline's attempt (10 s) and total (30 s) timeouts are the only bounds of a node
                    // call (WF08): an HttpClient timeout of its own would cut across them and across a retry.
                    client.Timeout = Timeout.InfiniteTimeSpan;

                    using var request = new HttpRequestMessage(method, route);
                    if (payload is not null)
                    {
                        request.Content = new ByteArrayContent(payload);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType) { CharSet = "utf-8" };
                    }

                    using var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, token);

                    var body = await ReadBodyAsync(response, token);

                    return new AdapterCallResult((int)response.StatusCode, body, RetryAfterOf(response));
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's cancellation is never a failure shape and is never retried (task §Design).
            throw;
        }
        catch (TimeoutRejectedException)
        {
            LogFailed(node.Name, method.Method, null, "timeout");
            return RenderClientErrors.ToError(
                AdapterErrorMapper.Map(RenderClientError.Timeout(), taskSubmitted, healthPoll));
        }
        catch (BrokenCircuitException)
        {
            // An open breaker is the node failing, which is what a transient failure means: the caller retries
            // through the job's policy instead of this call (BD6).
            LogFailed(node.Name, method.Method, null, "circuit_open");
            return RenderClientErrors.ToError(TransientNodeFailure());
        }
        catch (HttpRequestException)
        {
            // The message of a transport failure carries the request URI, so it is deliberately dropped here
            // and never logged (BD16/S4).
            LogFailed(node.Name, method.Method, null, "transport");
            return RenderClientErrors.ToError(
                AdapterErrorMapper.Map(RenderClientError.Transport(), taskSubmitted, healthPoll));
        }
        catch (OperationCanceledException)
        {
            // A timeout the pipeline reported as a cancellation rather than as a TimeoutRejectedException.
            LogFailed(node.Name, method.Method, null, "timeout");
            return RenderClientErrors.ToError(
                AdapterErrorMapper.Map(RenderClientError.Timeout(), taskSubmitted, healthPoll));
        }

        if (call.Status is >= 200 and < 300)
        {
            return call;
        }

        var failure = AdapterErrorMapper.Map(
            RenderClientError.Status(call.Status, CodeOf(call.Body), call.RetryAfter), taskSubmitted, healthPoll);

        LogFailed(node.Name, method.Method, call.Status, failure.OutcomeCode ?? failure.Kind.ToString());
        return RenderClientErrors.ToError(failure);
    }

    /// <summary>
    /// Reads an answer body up to <see cref="MaxBodyBytes"/>. Past the cap nothing is buffered at all: the
    /// answer then parses as no body, which the mapper turns into the fail-closed terminal failure of an
    /// unreadable answer, so a node cannot make the Worker hold an unbounded amount of memory for a status
    /// (BD16).
    /// </summary>
    /// <param name="response">The answer whose body is being read.</param>
    /// <param name="ct">The pipeline's token: the attempt and total timeouts both end this read.</param>
    /// <returns>The body, or an empty array when there was none or it was over the cap.</returns>
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, ChunkBytes), ct);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > MaxBodyBytes)
                {
                    return [];
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>The adapter's <c>Retry-After</c>, parsed by the one parser that accepts its integer-seconds form.</summary>
    /// <param name="response">The answer that may carry the header.</param>
    /// <returns>The wait, or <c>null</c> when the answer carried no usable header.</returns>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(RetryAfterHeaderName, out var values)
            ? AdapterErrorMapper.ParseRetryAfter(values.FirstOrDefault())
            : null;

    /// <summary>
    /// The adapter's own error code of an unreadable answer, or <c>null</c> when the body is not an envelope
    /// at all. Only the code travels: the envelope's <c>message</c> is never read, logged or copied (BD16/S4).
    /// </summary>
    /// <param name="body">The answer body, already capped.</param>
    /// <returns>The code, or <c>null</c>.</returns>
    private static string? CodeOf(byte[] body) => Parse<AdapterErrorV1>(body)?.Error.Code;

    /// <summary>Decodes one wire body with the tolerant profile, which ignores what a newer adapter adds.</summary>
    /// <typeparam name="T">The contract type the caller expects.</typeparam>
    /// <param name="body">The answer body, already capped.</param>
    /// <returns>The decoded body, or <c>null</c> when it is absent or unreadable.</returns>
    private static T? Parse<T>(byte[] body)
        where T : class
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, RenderNodeJson.Tolerant);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The route of one call: the contract's literal with its task-id parameter replaced by the escaped id.
    /// The id is escaped even though <see cref="IsSafeTaskId"/> has already refused everything that is not a
    /// plain adapter id, so no id can ever become a path of its own (S8/A03).
    /// </summary>
    /// <param name="template">The contract's route literal.</param>
    /// <param name="taskId">The already validated task id.</param>
    /// <returns>The relative route to send.</returns>
    private static Uri Route(string template, string taskId) =>
        new(
            template.Replace(
                "{" + RenderNodeRoutes.TaskIdParameter + "}",
                Uri.EscapeDataString(taskId),
                StringComparison.Ordinal),
            UriKind.Relative);

    /// <summary>Whether the id can be a path segment: non-empty, bounded and made of unreserved characters only.</summary>
    /// <param name="taskId">The id to check.</param>
    /// <returns><c>true</c> when the id may be put in a route.</returns>
    private static bool IsSafeTaskId(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId) || taskId.Length > TaskIdMaxLength)
        {
            return false;
        }

        foreach (var character in taskId)
        {
            // No dot: ".." in a path segment is exactly the traversal this check exists to refuse.
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A transient node failure, for the fault that has no status to map (an open breaker).</summary>
    /// <returns>The failure the caller switches on.</returns>
    private static RenderClientFailure TransientNodeFailure() =>
        new(
            RenderClientFailureKind.TransientFailure,
            RenderOutcomeCodes.RenderNodeError,
            Detail: null,
            Alert: false,
            RetryAfter: null);

    /// <summary>
    /// The failure of a 2xx whose body is not the contract type: contract drift, so it fails closed and
    /// alerts instead of being retried (task file §Failure handling).
    /// </summary>
    /// <param name="node">The node that answered.</param>
    /// <param name="status">Its status.</param>
    /// <param name="taskSubmitted">Whether the call concerned a submitted task.</param>
    /// <returns>The error to return.</returns>
    private Error Unreadable(WorkerNode node, int status, bool taskSubmitted)
    {
        LogUnreadable(node.Name, status);
        return RenderClientErrors.ToError(
            AdapterErrorMapper.Map(RenderClientError.Status(status), taskSubmitted, HealthPoll()));
    }

    /// <summary>
    /// The failure of a task id that was refused before any URL was built. It alerts: a caller that offers
    /// something other than an adapter id is a bug or an injection attempt, never a node fault.
    /// </summary>
    /// <param name="node">The node the call was aimed at.</param>
    /// <returns>The error to return.</returns>
    private Error RefusedTaskId(WorkerNode node)
    {
        LogRefusedTaskId(node.Name);
        return RenderClientErrors.ToError(
            new RenderClientFailure(
                RenderClientFailureKind.TerminalFailure,
                RenderOutcomeCodes.RenderNodeError,
                TaskIdInvalidDetail,
                Alert: true,
                RetryAfter: null));
    }

    /// <summary>The configured <c>RenderNodes:HealthPollSeconds</c> the mapper defers an unhealthy node by.</summary>
    /// <returns>The interval.</returns>
    private TimeSpan HealthPoll() => TimeSpan.FromSeconds(renderNodes.Value.HealthPollSeconds);

    [LoggerMessage(
        EventId = 4130,
        Level = LogLevel.Debug,
        Message = "The render node {Node} accepted the submission: task {TaskId} is {State}.")]
    private partial void LogAccepted(string node, Guid taskId, string state);

    [LoggerMessage(
        EventId = 4131,
        Level = LogLevel.Debug,
        Message = "The render node {Node} reported task {TaskId} as {State} ({Percent}%).")]
    private partial void LogStatus(string node, string taskId, string state, int percent);

    [LoggerMessage(
        EventId = 4132,
        Level = LogLevel.Information,
        Message = "The render node {Node} took the cancel for task {TaskId}.")]
    private partial void LogCancelled(string node, string taskId);

    [LoggerMessage(
        EventId = 4133,
        Level = LogLevel.Information,
        Message = "The render node {Node} took the delete for task {TaskId}.")]
    private partial void LogDeleted(string node, string taskId);

    [LoggerMessage(
        EventId = 4134,
        Level = LogLevel.Warning,
        Message = "The render node {Node} failed {Method} with {Reason} (status {Status}).")]
    private partial void LogFailed(string node, string method, int? status, string reason);

    [LoggerMessage(
        EventId = 4135,
        Level = LogLevel.Warning,
        Message = "The render node {Node} answered status {Status} with a body the contract cannot read.")]
    private partial void LogUnreadable(string node, int status);

    [LoggerMessage(
        EventId = 4136,
        Level = LogLevel.Warning,
        Message = "A render task id was refused for node {Node} before any URL was built.")]
    private partial void LogRefusedTaskId(string node);
}
